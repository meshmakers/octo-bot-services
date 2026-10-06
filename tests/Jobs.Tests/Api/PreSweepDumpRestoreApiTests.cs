using System.Net;
using System.Security.Cryptography;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.ArtifactStorage;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Api;

/// <summary>
///     AB#5559 through the real request pipeline: <c>POST {tenantId}/v1/secrets/sweep-runs/{runId}/restore-dump</c>
///     needs the role <c>SecretManagement</c>, <c>confirm=true</c> and the caller's own tenant.
/// </summary>
public class PreSweepDumpRestoreApiTests
{
    private const string Child = JobsApiTestHost.Child;
    private const string Unrelated = JobsApiTestHost.Unrelated;


    private static string RestorePath(string tenant = Child, string confirm = "?confirm=true")
    {
        return $"/{tenant}/v1/secrets/sweep-runs/run-1/restore-dump{confirm}";
    }

    [Test]
    public async Task RestoreDump_SecretManagementAndConfirm_EnqueuesTheJobForTheRouteTenant()
    {
        using var host = await JobsApiTestHost.StartAsync();
        host.SecretSweepRunService.CheckDumpRestorableAsync(Child, "run-1")
            .Returns(new SecretSweepDumpRestoreCheck(SecretSweepDumpRestoreState.Restorable, "d.presweep.octoenc"));

        var response = await host.PostAsync(RestorePath(),
            JobsApiTestHost.UserTokenWithRoles(Child, CommonConstants.SecretManagementRole));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var job = host.LastEnqueuedJob();
        await Assert.That(job.Type).IsEqualTo(typeof(IRestorePreSweepDumpJob));
        await Assert.That(job.Args[0]).IsEqualTo(Child);
        await Assert.That(job.Args[1]).IsEqualTo("run-1");
        await Assert.That(job.Args[2]).IsEqualTo(JobsApiTestHost.UserName);
        host.JobStorage.Received().SetJobParameter("job-1",
            Meshmakers.Octo.Backend.BotServices.Services.JobTenantBinding.StartedForTenantParameter, Child);
    }

    [Test]
    [Arguments("")]
    [Arguments("?confirm=false")]
    public async Task RestoreDump_WithoutConfirm_Is400_AndEnqueuesNothing(string confirm)
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.PostAsync(RestorePath(confirm: confirm),
            JobsApiTestHost.UserTokenWithRoles(Child, CommonConstants.SecretManagementRole));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("ConfirmationRequired");
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(0);
    }

    [Test]
    public async Task RestoreDump_WithoutSecretManagement_IsForbidden()
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.PostAsync(RestorePath(),
            JobsApiTestHost.UserTokenWithRoles(Child, CommonConstants.AdminPanelManagementRole));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(0);
        await host.SecretSweepRunService.DidNotReceiveWithAnyArgs().CheckDumpRestorableAsync(default!, default!);
    }

    [Test]
    public async Task RestoreDump_OfAnotherTenant_IsRefusedByTheTenantGate()
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.PostAsync(RestorePath(Unrelated),
            JobsApiTestHost.UserTokenWithRoles(Child, CommonConstants.SecretManagementRole));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(SecretSweepDumpRestoreState.NotFound, HttpStatusCode.NotFound, "")]
    [Arguments(SecretSweepDumpRestoreState.Missing, HttpStatusCode.NotFound, "")]
    [Arguments(SecretSweepDumpRestoreState.Deleted, HttpStatusCode.Conflict, "DumpDeleted")]
    [Arguments(SecretSweepDumpRestoreState.KeyMissing, HttpStatusCode.Conflict, "DumpKeyMissing")]
    public async Task RestoreDump_NotRestorable_MapsTheState_AndEnqueuesNothing(SecretSweepDumpRestoreState state,
        HttpStatusCode expected, string code)
    {
        using var host = await JobsApiTestHost.StartAsync();
        host.SecretSweepRunService.CheckDumpRestorableAsync(Child, "run-1")
            .Returns(new SecretSweepDumpRestoreCheck(state, "d.presweep.octoenc", "k0"));

        var response = await host.PostAsync(RestorePath(),
            JobsApiTestHost.UserTokenWithRoles(Child, CommonConstants.SecretManagementRole));

        await Assert.That(response.StatusCode).IsEqualTo(expected);
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains(code);
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(0);
    }
}
