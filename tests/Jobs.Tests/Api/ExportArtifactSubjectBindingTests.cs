using System.Net;
using Meshmakers.Octo.Backend.BotServices.Consumers;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Common.DistributionEventHub.Payloads;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Api;

/// <summary>
///     AB#6734 — the result of a runtime-data export belongs to the user who requested it. Another subject of the
///     same tenant who learned the job id gets 403 and no data on download, status and delete; the initiating
///     subject (a client-credentials token counts as its client id) and the parent-tenant administration rule of
///     AB#5070 keep their access. Every test enqueues through the REAL consumer first, so the binding is read from
///     the very job the platform produced.
/// </summary>
internal class ExportArtifactSubjectBindingTests
{
    private const string Child = JobsApiTestHost.Child;
    private const string Parent = JobsApiTestHost.Parent;
    private const string JobId = "6512a1b2c3d4e5f60102bbbb";
    private const string CacheKey = "export-result-key";
    private const string Payload = "RtEntities-payload";

    private static ExportRtByQueryCommandRequest QueryExport(string? subject) =>
        new(Child, OctoObjectId.GenerateNewId())
        {
            InitiatedBySubjectId = subject,
            InitiatedByRoleNames = ["Reader"]
        };

    private static async Task SeedExportJobAsync(JobsApiTestHost host, string initiatingSubject)
    {
        // The real consumer enqueues the real job (the host's job client records the Create call).
        var consumer = new ModelCommandsConsumer(host.BackgroundJobClient);
        var context = Substitute.For<IDistributedContext<ExportRtByQueryCommandRequest>>();
        context.Message.Returns(QueryExport(initiatingSubject));
        await consumer.ConsumeAsync(context);

        host.SeedSucceededJob(JobId, host.LastEnqueuedJob(), CacheKey);
        host.DistributedCache.GetCacheStreamByIdAsync(Child, CacheKey).Returns(_ => new CacheStream
        {
            Stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Payload)),
            ContentType = "application/zip",
            FileName = "RtEntities.zip"
        });
    }

    /// <summary>
    ///     The binding and the job's caller context live in the Hangfire job arguments, which storage serializes
    ///     (Newtonsoft) and the dashboard/controllers read back: subject, roles and client must survive that round trip.
    /// </summary>
    [Test]
    public async Task CallerContext_SurvivesTheHangfireInvocationRoundTrip()
    {
        var request = new ExportRtByQueryCommandRequest(Child, OctoObjectId.GenerateNewId())
        {
            InitiatedBySubjectId = "alice",
            InitiatedByRoleNames = ["Reader", "Analyst"],
            InitiatedByClientId = "studio"
        };
        var job = Hangfire.Common.Job.FromExpression<Meshmakers.Octo.Backend.Jobs.Jobs.IExportModelJob>(j =>
            j.ExportRtModelByQueryAsync(request, null));

        var stored = Hangfire.Storage.InvocationData.SerializeJob(job);
        var back = stored.DeserializeJob();

        await Assert.That(Meshmakers.Octo.Backend.BotServices.Services.JobTenantBinding.IsInitiatorBound(back)).IsTrue();
        await Assert.That(Meshmakers.Octo.Backend.BotServices.Services.JobTenantBinding.TryResolveInitiatingSubject(back))
            .IsEqualTo("alice");
        await Assert.That(Meshmakers.Octo.Backend.BotServices.Services.JobTenantBinding.TryResolveTenantId(back))
            .IsEqualTo(Child);
        var args = (ExportRtByQueryCommandRequest)back.Args[0]!;
        await Assert.That(args.InitiatedByRoleNames!).IsEquivalentTo(new[] { "Reader", "Analyst" });
        await Assert.That(args.InitiatedByClientId).IsEqualTo("studio");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Download_AnotherSubjectOfTheSameTenant_GetsNoData(bool tenantRoute)
    {
        using var host = await JobsApiTestHost.StartAsync();
        await SeedExportJobAsync(host, "alice");
        var url = tenantRoute
            ? $"/{Child}/v1/jobs/download?id={JobId}"
            : $"/system/v1/jobs/download?tenantId={Child}&id={JobId}";

        var other = await host.GetAsync(url, JobsApiTestHost.UserTokenAs(Child, "bob"));

        await Assert.That(other.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await other.Content.ReadAsStringAsync()).Contains(Payload)).IsFalse();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Download_TheInitiatingSubject_GetsTheExport(bool tenantRoute)
    {
        using var host = await JobsApiTestHost.StartAsync();
        await SeedExportJobAsync(host, "alice");
        var url = tenantRoute
            ? $"/{Child}/v1/jobs/download?id={JobId}"
            : $"/system/v1/jobs/download?tenantId={Child}&id={JobId}";

        var own = await host.GetAsync(url, JobsApiTestHost.UserTokenAs(Child, "alice"));

        await Assert.That(own.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await own.Content.ReadAsStringAsync()).IsEqualTo(Payload);
    }

    [Test]
    public async Task Download_ParentTenantAdministrator_KeepsTheAccessOfAB5070()
    {
        using var host = await JobsApiTestHost.StartAsync();
        await SeedExportJobAsync(host, "alice");

        var onChildRoute = await host.GetAsync($"/{Child}/v1/jobs/download?id={JobId}",
            JobsApiTestHost.UserTokenAs(Parent, "parent-admin"));
        var onSystemRoute = await host.GetAsync($"/system/v1/jobs/download?tenantId={Child}&id={JobId}",
            JobsApiTestHost.UserTokenAs(Parent, "parent-admin"));

        await Assert.That(onChildRoute.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(onSystemRoute.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Download_ForeignTenantUserWhoIsNoAncestor_IsStillRefused()
    {
        // Observation mode of the tenant gate lets a foreign token through with a warning; the subject binding
        // must not widen that to a subject-less export artifact.
        using var host = await JobsApiTestHost.StartAsync(
            o => o.UserTokenEnforcement = Meshmakers.Octo.Services.Infrastructure.Configuration
                .UserTokenTenantEnforcementMode.LogOnly);
        await SeedExportJobAsync(host, "alice");

        var response = await host.GetAsync($"/system/v1/jobs/download?tenantId={Child}&id={JobId}",
            JobsApiTestHost.UserTokenAs(JobsApiTestHost.Unrelated, "mallory"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task ClientCredentialsInitiator_IsBoundToItsClientId()
    {
        using var host = await JobsApiTestHost.StartAsync();
        await SeedExportJobAsync(host, "pipeline-client");

        var sameClient = await host.GetAsync($"/{Child}/v1/jobs/download?id={JobId}",
            JobsApiTestHost.ServiceTokenOfClient(Child, "pipeline-client"));
        var otherClient = await host.GetAsync($"/{Child}/v1/jobs/download?id={JobId}",
            JobsApiTestHost.ServiceTokenOfClient(Child, "other-client"));

        await Assert.That(sameClient.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(otherClient.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task StatusAndDelete_AreBoundToTheInitiatorToo()
    {
        using var host = await JobsApiTestHost.StartAsync();
        await SeedExportJobAsync(host, "alice");

        var statusOther = await host.GetAsync($"/system/v1/jobs?id={JobId}", JobsApiTestHost.UserTokenAs(Child, "bob"));
        var deleteOther = await host.DeleteAsync($"/system/v1/jobs/{JobId}", JobsApiTestHost.UserTokenAs(Child, "bob"));
        var statusOwn = await host.GetAsync($"/system/v1/jobs?id={JobId}", JobsApiTestHost.UserTokenAs(Child, "alice"));

        await Assert.That(statusOther.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(deleteOther.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(statusOwn.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task OtherJobTypes_StayBoundToTheTenantOnly()
    {
        // A dump is a whole-tenant backup (AB#4497): every tenant administrator may fetch it, not only the one who
        // started it. The subject binding is for the runtime-data exports.
        using var host = await JobsApiTestHost.StartAsync();
        var start = await host.PostAsync($"/{Child}/v1/jobs/dump-repository", JobsApiTestHost.UserTokenAs(Child, "alice"));
        await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "dump");
        try
        {
            host.SeedSucceededJob(JobId, host.LastEnqueuedJob(), path);

            var other = await host.GetAsync($"/{Child}/v1/jobs/download?id={JobId}",
                JobsApiTestHost.UserTokenAs(Child, "bob"));

            await Assert.That(other.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
