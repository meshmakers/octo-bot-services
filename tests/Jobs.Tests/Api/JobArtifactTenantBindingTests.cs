using System.Net;
using Hangfire.Common;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Api;

/// <summary>
///     AB#5070 — a job artifact belongs to the tenant the job ran for, and every job-instance endpoint
///     (status, download, delete) enforces that.
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>The hole these tests close.</b> <c>system/v1/jobs/download?tenantId=…&amp;id=…</c>
///         carries no tenant route segment, so <c>TenantAuthorizationMiddleware</c> returns early and
///         checks nothing — "unmarked" there means <b>unchecked</b>, not "exactly matched". And the
///         lookup resolved the result file from the job id alone; the <c>tenantId</c> argument was
///         consulted only by the legacy GridFS fallback. Any caller holding the job-read scope could
///         therefore fetch any tenant's backup with a job id, which AB#5060 made concrete: a parent
///         administrator legitimately receives the job id of a child tenant's dump.
///     </para>
///     <para>
///         The binding is the job's own stored arguments (<see cref="JobTenantBinding" />), which is
///         why nearly every test below <b>enqueues through the real endpoint first</b> and seeds the
///         very <see cref="Job" /> the controller produced: a hand-made job would test the test.
///     </para>
/// </remarks>
internal class JobArtifactTenantBindingTests
{
    private const string Parent = JobsApiTestHost.Parent;
    private const string Child = JobsApiTestHost.Child;
    private const string Unrelated = JobsApiTestHost.Unrelated;

    private const string JobId = "6512a1b2c3d4e5f60102aaaa";

    // ---------------------------------------------------------------------------------------------
    // The System route — no route tenant, so the endpoint performs the check itself.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    ///     🔴 <b>The negative case AB#5070 exists for.</b> A valid job id of the child tenant, a valid
    ///     token with the job-read scope — but issued for a tenant that is neither the job's tenant nor
    ///     its parent. Before AB#5070 this streamed the child's dump.
    /// </summary>
    [Test]
    public async Task SystemRoute_Download_ForeignTenantUserToken_GetsNoArtifact()
    {
        using var host = await JobsApiTestHost.StartAsync();
        using var artifact = new TempArtifact();
        await SeedDumpJobAsync(host, Child, artifact.Path);

        var response = await host.GetAsync($"/system/v1/jobs/download?tenantId={Unrelated}&id={JobId}",
            JobsApiTestHost.UserToken(Unrelated));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await response.Content.ReadAsStringAsync()).Contains(TempArtifact.Content)).IsFalse();
    }

    /// <summary>
    ///     🔴 The caller-supplied <c>tenantId</c> is not the binding and cannot become one. Here the
    ///     caller names the job's <i>real</i> tenant in the query — the one piece of information an
    ///     attacker holding the job id would also have — and is still refused, because the decision is
    ///     made against the <c>tenant_id</c> claim of the token, never against a query argument.
    /// </summary>
    [Test]
    public async Task SystemRoute_Download_QueryTenantArgumentCannotWidenAccess()
    {
        using var host = await JobsApiTestHost.StartAsync();
        using var artifact = new TempArtifact();
        await SeedDumpJobAsync(host, Child, artifact.Path);

        var response = await host.GetAsync($"/system/v1/jobs/download?tenantId={Child}&id={JobId}",
            JobsApiTestHost.UserToken(Unrelated));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    /// <summary>The owner keeps their artifact — the check narrows, it does not break the endpoint.</summary>
    [Test]
    public async Task SystemRoute_Download_OwnTenantUserToken_StreamsTheArtifact()
    {
        using var host = await JobsApiTestHost.StartAsync();
        using var artifact = new TempArtifact();
        await SeedDumpJobAsync(host, Child, artifact.Path);

        var response = await host.GetAsync($"/system/v1/jobs/download?tenantId={Child}&id={JobId}",
            JobsApiTestHost.UserToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(TempArtifact.Content);
    }

    /// <summary>
    ///     🔴 Securing a child tenant encloses the file. The parent administrator who may start the
    ///     child's dump (AB#5060) may fetch it, on the System route too — the guard applies the same
    ///     ancestor rule the middleware would have applied had the route carried the tenant.
    /// </summary>
    [Test]
    public async Task SystemRoute_Download_ParentUserToken_StreamsTheChildArtifact()
    {
        using var host = await JobsApiTestHost.StartAsync();
        using var artifact = new TempArtifact();
        await SeedDumpJobAsync(host, Child, artifact.Path);

        var response = await host.GetAsync($"/system/v1/jobs/download?tenantId={Child}&id={JobId}",
            JobsApiTestHost.UserToken(Parent));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(TempArtifact.Content);
    }

    /// <summary>
    ///     🔴 A <b>service</b> token of the parent gets nothing. This is what makes the ancestor rule
    ///     safe at all: a client-credentials <c>tenant_id</c> proves nothing — mirrored clients share
    ///     the parent's secret, and a token minted without <c>acr_values</c> falls back to the system
    ///     tenant, i.e. the root of the hierarchy.
    /// </summary>
    [Test]
    public async Task SystemRoute_Download_ParentServiceToken_IsNotAllowedByTheAncestorRule()
    {
        using var host = await JobsApiTestHost.StartAsync(
            o => o.ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Enforce);
        using var artifact = new TempArtifact();
        await SeedDumpJobAsync(host, Child, artifact.Path);

        var response = await host.GetAsync($"/system/v1/jobs/download?tenantId={Child}&id={JobId}",
            JobsApiTestHost.ServiceToken(Parent));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    /// <summary>
    ///     A service token issued for the job's own tenant keeps working: the rule removes the ancestor
    ///     shortcut, not the exact match a deployed worker or a CI login relies on.
    /// </summary>
    [Test]
    public async Task SystemRoute_Download_OwnServiceToken_StreamsTheArtifact()
    {
        using var host = await JobsApiTestHost.StartAsync(
            o => o.ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Enforce);
        using var artifact = new TempArtifact();
        await SeedDumpJobAsync(host, Child, artifact.Path);

        var response = await host.GetAsync($"/system/v1/jobs/download?tenantId={Child}&id={JobId}",
            JobsApiTestHost.ServiceToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    ///     🔴 <b>The residual exposure, pinned rather than argued.</b> With the platform default
    ///     <c>ServiceTokenEnforcement = LogOnly</c> a foreign <b>service</b> token still gets the
    ///     artifact — logged, not refused. That is deliberate: this guard is a port of
    ///     <c>TenantAuthorizationMiddleware</c>, staging included, so one environment switch
    ///     (<c>OCTO_TENANTAUTHORIZATION__SERVICETOKENENFORCEMENT=Enforce</c>) governs both surfaces and
    ///     the artifact path cannot end up stricter than every tenant route of the same service. The
    ///     user path — the one AB#5060 made concrete, and the one a human uses — is closed today,
    ///     because <c>UserTokenEnforcement</c> defaults to <c>Enforce</c> and this service never opts
    ///     down. Deleting this test without deleting the staging would hide the gap, not close it.
    /// </summary>
    [Test]
    public async Task SystemRoute_Download_ForeignServiceToken_IsStagedExactlyLikeTheMiddleware()
    {
        using var host = await JobsApiTestHost.StartAsync();
        using var artifact = new TempArtifact();
        await SeedDumpJobAsync(host, Child, artifact.Path);

        var logOnly = await host.GetAsync($"/system/v1/jobs/download?tenantId={Child}&id={JobId}",
            JobsApiTestHost.ServiceToken(Unrelated));
        await Assert.That(logOnly.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var enforcing = await JobsApiTestHost.StartAsync(
            o => o.ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Enforce);
        await SeedDumpJobAsync(enforcing, Child, artifact.Path);

        var enforced = await enforcing.GetAsync($"/system/v1/jobs/download?tenantId={Child}&id={JobId}",
            JobsApiTestHost.ServiceToken(Unrelated));
        await Assert.That(enforced.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    /// <summary>
    ///     🔴 Fail closed. A job whose stored invocation cannot be deserialized has no determinable
    ///     tenant, and an artifact that cannot be attributed to a tenant is handed to nobody — not even
    ///     to a caller who names the right tenant in the query.
    /// </summary>
    [Test]
    public async Task SystemRoute_Download_JobWithUnreadableInvocation_IsForbidden()
    {
        using var host = await JobsApiTestHost.StartAsync();
        host.SeedJobWithUnreadableInvocation(JobId);

        var response = await host.GetAsync($"/system/v1/jobs/download?tenantId={Child}&id={JobId}",
            JobsApiTestHost.UserToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    /// <summary>An unknown job id still answers 404, as before — the gate adds a refusal, not a lie.</summary>
    [Test]
    public async Task SystemRoute_Download_UnknownJob_IsNotFound()
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.GetAsync($"/system/v1/jobs/download?tenantId={Child}&id={JobId}",
            JobsApiTestHost.UserToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------------------------------------
    // The tenant route — the middleware checks the caller, the endpoint checks ownership.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The new route, happy path: the job of the route tenant, fetched by its own admin.</summary>
    [Test]
    public async Task TenantRoute_Download_OwnTenantUserToken_StreamsTheArtifact()
    {
        using var host = await JobsApiTestHost.StartAsync();
        using var artifact = new TempArtifact();
        await SeedDumpJobAsync(host, Child, artifact.Path);

        var response = await host.GetAsync($"/{Child}/v1/jobs/download?id={JobId}",
            JobsApiTestHost.UserToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(TempArtifact.Content);
    }

    /// <summary>
    ///     The parent administrator reaches the child's artifact on the child's route, through the
    ///     class-level <c>[AllowParentTenantAdministration]</c> — the middleware admits the caller, the
    ///     endpoint confirms the job is the child's.
    /// </summary>
    [Test]
    public async Task TenantRoute_Download_ParentUserToken_StreamsTheChildArtifact()
    {
        using var host = await JobsApiTestHost.StartAsync();
        using var artifact = new TempArtifact();
        await SeedDumpJobAsync(host, Child, artifact.Path);

        var response = await host.GetAsync($"/{Child}/v1/jobs/download?id={JobId}",
            JobsApiTestHost.UserToken(Parent));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    ///     🔴 Ownership is checked separately from the caller. Here the caller is the undisputed
    ///     administrator of the route tenant — the middleware lets them straight through — but the job
    ///     belongs to somebody else, so the artifact is not addressable under this route.
    /// </summary>
    [Test]
    public async Task TenantRoute_Download_JobOfAnotherTenant_IsForbidden()
    {
        using var host = await JobsApiTestHost.StartAsync();
        using var artifact = new TempArtifact();
        await SeedDumpJobAsync(host, Unrelated, artifact.Path);

        var response = await host.GetAsync($"/{Child}/v1/jobs/download?id={JobId}",
            JobsApiTestHost.UserToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        // ... and the ownership check has no staging: this host runs the most permissive service-token
        // mode there is (the platform default LogOnly) and the answer is the same.
        var serviceResponse = await host.GetAsync($"/{Child}/v1/jobs/download?id={JobId}",
            JobsApiTestHost.ServiceToken(Child));

        await Assert.That(serviceResponse.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    /// <summary>
    ///     🔴 The ancestor relaxation lives in the middleware and stays there. A parent addressing the
    ///     <i>parent's</i> route may not reach a job of the child through it: the route says which
    ///     tenant is being administered, and the job must belong to that one.
    /// </summary>
    [Test]
    public async Task TenantRoute_Download_ChildJobOnParentRoute_IsForbidden()
    {
        using var host = await JobsApiTestHost.StartAsync();
        using var artifact = new TempArtifact();
        await SeedDumpJobAsync(host, Child, artifact.Path);

        var response = await host.GetAsync($"/{Parent}/v1/jobs/download?id={JobId}",
            JobsApiTestHost.UserToken(Parent));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    /// <summary>The tenant gate still runs first: an unrelated caller never reaches the endpoint.</summary>
    [Test]
    public async Task TenantRoute_Download_UnrelatedUserToken_IsForbidden()
    {
        using var host = await JobsApiTestHost.StartAsync();
        using var artifact = new TempArtifact();
        await SeedDumpJobAsync(host, Child, artifact.Path);

        var response = await host.GetAsync($"/{Child}/v1/jobs/download?id={JobId}",
            JobsApiTestHost.UserToken(Unrelated));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    // ---------------------------------------------------------------------------------------------
    // Status and delete — less than an artifact, but not nothing.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    ///     A job status carries the failure message of the job, which for a restore or an archive
    ///     export names database names, file names and archive ids of the tenant it ran for. Hangfire
    ///     job ids are Mongo ObjectIds and therefore partly guessable, so the status is gated with the
    ///     same rule as the artifact.
    /// </summary>
    [Test]
    public async Task SystemRoute_Get_ForeignTenantUserToken_IsForbidden()
    {
        using var host = await JobsApiTestHost.StartAsync();
        await SeedDumpJobAsync(host, Child, resultPath: null);

        var response = await host.GetAsync($"/system/v1/jobs?id={JobId}", JobsApiTestHost.UserToken(Unrelated));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    /// <summary>The owner still reads their own job status.</summary>
    [Test]
    public async Task SystemRoute_Get_OwnTenantUserToken_IsAllowed()
    {
        using var host = await JobsApiTestHost.StartAsync();
        await SeedDumpJobAsync(host, Child, resultPath: null);

        var response = await host.GetAsync($"/system/v1/jobs?id={JobId}", JobsApiTestHost.UserToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    ///     🔴 Delete is gated for a stronger reason than the status read: it is a mutation. An ungated
    ///     delete lets any holder of the job-write scope cancel another tenant's running restore, so a
    ///     refused delete must also not have changed any state.
    /// </summary>
    [Test]
    public async Task SystemRoute_Delete_ForeignTenantUserToken_IsForbiddenAndChangesNothing()
    {
        using var host = await JobsApiTestHost.StartAsync();
        await SeedDumpJobAsync(host, Child, resultPath: null);
        host.ResetJobClient();

        var response = await host.DeleteAsync($"/system/v1/jobs/{JobId}", JobsApiTestHost.UserToken(Unrelated));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        host.BackgroundJobClient.DidNotReceive()
            .ChangeState(Arg.Any<string>(), Arg.Any<Hangfire.States.IState>(), Arg.Any<string>());
    }

    /// <summary>The owner still deletes their own job.</summary>
    [Test]
    public async Task SystemRoute_Delete_OwnTenantUserToken_IsAllowed()
    {
        using var host = await JobsApiTestHost.StartAsync();
        await SeedDumpJobAsync(host, Child, resultPath: null);
        host.ResetJobClient();

        var response = await host.DeleteAsync($"/system/v1/jobs/{JobId}", JobsApiTestHost.UserToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        host.BackgroundJobClient.Received()
            .ChangeState(JobId, Arg.Any<Hangfire.States.IState>(), Arg.Any<string>());
    }

    // ---------------------------------------------------------------------------------------------
    // The binding itself.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    ///     Every job this service enqueues from a controller takes its tenant as the first argument,
    ///     and that argument is what the binding reads. Checked against the real jobs the real
    ///     endpoints enqueue, so a renamed or reordered parameter fails here rather than silently
    ///     turning a job into an unattributable one.
    /// </summary>
    [Test]
    [Arguments("run-fixup-scripts", "")]
    [Arguments("dump-repository", "")]
    [Arguments("export-archive-data", "?archiveRtId=6512a1b2c3d4e5f601020304")]
    [Arguments("restore-from-upload", "?tusFileId=upload-1&databaseName=octo-child")]
    [Arguments("import-archive-data-from-upload", "?tusFileId=upload-1&archiveRtId=6512a1b2c3d4e5f601020304")]
    public async Task EveryEnqueuedJobCarriesItsTenantInItsArguments(string route, string query)
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.PostAsync($"/{Child}/v1/jobs/{route}{query}",
            JobsApiTestHost.UserToken(Child));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        await Assert.That(JobTenantBinding.TryResolveTenantId(host.LastEnqueuedJob())).IsEqualTo(Child);
    }

    /// <summary>
    ///     The two runtime-model exports arrive over the bus and take the whole command request instead
    ///     of a bare tenant id — the binding reads <see cref="CommandBaseRequest.TenantId" /> off it.
    ///     Those two are the original consumers of the download endpoint, so leaving them
    ///     unattributable would have left the hole open for exactly the artifact it was built for.
    /// </summary>
    [Test]
    public async Task BusEnqueuedExportBindsToTheCommandRequestTenant()
    {
        var job = new Job(
            typeof(IExportModelJob),
            typeof(IExportModelJob).GetMethod(nameof(IExportModelJob.ExportRtModelByQueryAsync))!,
            new object?[]
            {
                new ExportRtByQueryCommandRequest(Child, new OctoObjectId("6512a1b2c3d4e5f601020304")),
                null
            }!);

        await Assert.That(JobTenantBinding.TryResolveTenantId(job)).IsEqualTo(Child);
    }

    /// <summary>
    ///     An instance-wide job has no tenant at all, and the binding says so instead of inventing one.
    ///     Its artifact — it has none — is therefore reachable by nobody, which is the fail-closed
    ///     direction.
    /// </summary>
    [Test]
    public async Task InstanceWideJobHasNoTenant()
    {
        var job = new Job(
            typeof(ICleanupStaleFilesJob),
            typeof(ICleanupStaleFilesJob).GetMethod(nameof(ICleanupStaleFilesJob.Run))!,
            new object?[] { null }!);

        await Assert.That(JobTenantBinding.TryResolveTenantId(job)).IsNull();
        await Assert.That(JobTenantBinding.TryResolveTenantId(null)).IsNull();
    }

    /// <summary>
    ///     🔴 The starting subject is recorded on the job but is <b>not</b> the binding: granularity is
    ///     the tenant, so a second administrator of the same tenant and a dump started by CI stay
    ///     reachable. Recording it now keeps a later, finer rule from needing a data migration.
    /// </summary>
    [Test]
    public async Task EnqueueRecordsTheStartingSubjectWithoutBindingTheArtifactToIt()
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.PostAsync($"/{Child}/v1/jobs/dump-repository", JobsApiTestHost.UserToken(Child));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        host.JobStorage.Received().SetJobParameter("job-1", JobTenantBinding.StartedBySubjectParameter,
            "test-subject");
        host.JobStorage.Received().SetJobParameter("job-1", JobTenantBinding.StartedByClientIdParameter,
            "octo-cli");
        host.JobStorage.Received().SetJobParameter("job-1", JobTenantBinding.StartedForTenantParameter, Child);

        // ... and a second administrator of the same tenant still gets the artifact.
        using var artifact = new TempArtifact();
        host.SeedSucceededJob(JobId, host.LastEnqueuedJob(), artifact.Path);

        var download = await host.GetAsync($"/{Child}/v1/jobs/download?id={JobId}",
            JobsApiTestHost.UserToken(Child));

        await Assert.That(download.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    ///     Enqueues a repository dump for <paramref name="tenantId" /> through the real endpoint and
    ///     seeds the resulting Hangfire job under <see cref="JobId" />, so the tenant binding under
    ///     test reads the arguments production would have written.
    /// </summary>
    private static async Task SeedDumpJobAsync(JobsApiTestHost host, string tenantId, string? resultPath)
    {
        var response = await host.PostAsync($"/{tenantId}/v1/jobs/dump-repository",
            JobsApiTestHost.UserToken(tenantId));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        host.SeedSucceededJob(JobId, host.LastEnqueuedJob(), resultPath);
    }

    /// <summary>A real file on disk standing in for the dump the job left behind.</summary>
    private sealed class TempArtifact : IDisposable
    {
        public const string Content = "octo-backup-payload";

        public TempArtifact()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"octo-bot-artifact-{Guid.NewGuid():N}.tar.gz");
            File.WriteAllText(Path, Content);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
