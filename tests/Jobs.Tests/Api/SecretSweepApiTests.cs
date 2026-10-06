using System.Net;
using System.Text.Json;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Api;

/// <summary>
///     AB#5539 — the secret sweep endpoints: tenant route <c>{tenantId}/v1/jobs/secret-sweep[/report]</c> and
///     the instance-wide <c>system/v1/secrets/...</c>, through the real request pipeline.
/// </summary>
internal class SecretSweepApiTests
{
    private const string Child = JobsApiTestHost.Child;
    private const string Unrelated = JobsApiTestHost.Unrelated;
    private const string SystemTenant = "octosystem";

    [Test]
    [Arguments("Verify", SecretSweepMode.Verify)]
    [Arguments("Encrypt&confirm=true", SecretSweepMode.Encrypt)]
    [Arguments("Reprotect", SecretSweepMode.Reprotect)]
    [Arguments("CleanupUnreadable&confirm=true", SecretSweepMode.CleanupUnreadable)]
    [Arguments("3&confirm=true", SecretSweepMode.CleanupUnreadable)]
    [Arguments("1&confirm=true", SecretSweepMode.Encrypt)]
    public async Task TenantSweep_EnqueuesTheSweepJobWithTheMode(string modeQuery, SecretSweepMode expected)
    {
        using var host = await JobsApiTestHost.StartAsync();
        host.ResetJobClient();

        var response = await host.PostAsync($"/{Child}/v1/jobs/secret-sweep?mode={modeQuery}",
            JobsApiTestHost.UserTokenWithRoles(Child, CommonConstants.SecretManagementRole));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var job = host.LastEnqueuedJob();
        await Assert.That(job.Type).IsEqualTo(typeof(ISecretSweepJob));
        await Assert.That(job.Method.Name).IsEqualTo(nameof(ISecretSweepJob.Run));
        await Assert.That(job.Args[0]).IsEqualTo(Child);
        await Assert.That(job.Args[1]).IsEqualTo(expected);
        // The starter's user name goes into the run history (triggeredBy).
        await Assert.That(job.Args[2]).IsEqualTo(JobsApiTestHost.UserName);
    }

    [Test]
    [Arguments("Encrypt")]
    [Arguments("CleanupUnreadable")]
    [Arguments("Encrypt&confirm=false")]
    [Arguments("CleanupUnreadable&confirm=false")]
    public async Task TenantSweep_ChangingModeWithoutConfirm_Is400ConfirmationRequired_AndEnqueuesNothing(
        string modeQuery)
    {
        using var host = await JobsApiTestHost.StartAsync();
        host.ResetJobClient();

        var response = await host.PostAsync($"/{Child}/v1/jobs/secret-sweep?mode={modeQuery}",
            JobsApiTestHost.UserToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(document.RootElement.GetProperty("statusDescription").GetString())
            .IsEqualTo("ConfirmationRequired");
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(0);
    }

    [Test]
    [Arguments("Verify")]
    [Arguments("Encrypt&confirm=true")]
    [Arguments("CleanupUnreadable&confirm=true")]
    public async Task TenantSweep_WithoutSecretManagementRole_IsForbidden_AndEnqueuesNothing(string modeQuery)
    {
        using var host = await JobsApiTestHost.StartAsync();
        host.ResetJobClient();

        // AdminPanelManagement alone does not allow triggering a sweep.
        var response = await host.PostAsync($"/{Child}/v1/jobs/secret-sweep?mode={modeQuery}",
            JobsApiTestHost.UserTokenWithRoles(Child, CommonConstants.AdminPanelManagementRole));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(0);
    }

    [Test]
    public async Task TenantReport_WithoutAdminPanelManagementRole_IsForbidden()
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.GetAsync($"/{Child}/v1/jobs/secret-sweep/report",
            JobsApiTestHost.UserTokenWithRoles(Child, CommonConstants.SecretManagementRole));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task TenantSweep_WithoutMode_DefaultsToVerify()
    {
        using var host = await JobsApiTestHost.StartAsync();
        host.ResetJobClient();

        var response = await host.PostAsync($"/{Child}/v1/jobs/secret-sweep", JobsApiTestHost.UserToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(host.LastEnqueuedJob().Args[1]).IsEqualTo(SecretSweepMode.Verify);
    }

    [Test]
    public async Task TenantSweep_Decrypt_IsRefusedAndEnqueuesNothing()
    {
        using var host = await JobsApiTestHost.StartAsync();
        host.ResetJobClient();

        var response = await host.PostAsync($"/{Child}/v1/jobs/secret-sweep?mode=Decrypt&confirm=true",
            JobsApiTestHost.UserToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(0);
    }

    [Test]
    public async Task TenantReport_None_Answers404()
    {
        using var host = await JobsApiTestHost.StartAsync();
        host.SecretSweepReportStore.GetLastAsync(Child).Returns((SecretSweepReport?)null);

        var response = await host.GetAsync($"/{Child}/v1/jobs/secret-sweep/report", JobsApiTestHost.UserToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task TenantReport_ReturnsTheStoredReportWithEnumNames()
    {
        using var host = await JobsApiTestHost.StartAsync();
        host.SecretSweepReportStore.GetLastAsync(Child).Returns(new SecretSweepReport
        {
            TenantId = Child,
            Mode = SecretSweepMode.Encrypt,
            Outcome = SecretSweepOutcome.Succeeded,
            PlaceholdersNormalized = 2,
            Unreadable =
            [
                new SecretUnreadableValueReport
                {
                    CkTypeId = "System.Communication/SftpConfiguration",
                    RtId = "6512a1b2c3d4e5f601020304",
                    AttributePath = "Password",
                    KeyId = "k9"
                }
            ],
            SecretsToReEnter =
            [
                new SecretValueReference
                {
                    CkTypeId = "System.Communication/SftpConfiguration",
                    RtId = "6512a1b2c3d4e5f601020304",
                    AttributePath = "Password",
                    PreviousForm = SecretValueForm.UnknownKeyId,
                    KeyId = "k9"
                }
            ]
        });

        var response = await host.GetAsync($"/{Child}/v1/jobs/secret-sweep/report", JobsApiTestHost.UserToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        await Assert.That(root.GetProperty("tenantId").GetString()).IsEqualTo(Child);
        await Assert.That(root.GetProperty("mode").GetString()).IsEqualTo("Encrypt");
        await Assert.That(root.GetProperty("outcome").GetString()).IsEqualTo("Succeeded");
        var reEnter = root.GetProperty("secretsToReEnter")[0];
        await Assert.That(reEnter.GetProperty("previousForm").GetString()).IsEqualTo("UnknownKeyId");
        await Assert.That(reEnter.GetProperty("attributePath").GetString()).IsEqualTo("Password");
        await Assert.That(root.GetProperty("placeholdersNormalized").GetInt64()).IsEqualTo(2);
        var unreadable = root.GetProperty("unreadable")[0];
        await Assert.That(unreadable.GetProperty("ckTypeId").GetString())
            .IsEqualTo("System.Communication/SftpConfiguration");
        await Assert.That(unreadable.GetProperty("rtId").GetString()).IsEqualTo("6512a1b2c3d4e5f601020304");
        await Assert.That(unreadable.GetProperty("attributePath").GetString()).IsEqualTo("Password");
        await Assert.That(unreadable.GetProperty("keyId").GetString()).IsEqualTo("k9");

        // The SDK contract reads the very same JSON.
        var dto = JsonSerializer.Deserialize<SecretSweepReportDto>(document.RootElement.GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        await Assert.That(dto.Unreadable.Single().KeyId).IsEqualTo("k9");
        await Assert.That(dto.PlaceholdersNormalized).IsEqualTo(2);
        await Assert.That(dto.SecretsToReEnter.Single().PreviousForm).IsEqualTo(SecretValueFormDto.UnknownKeyId);
    }

    [Test]
    public async Task TenantReport_UnrelatedTenant_IsForbidden()
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.GetAsync($"/{Child}/v1/jobs/secret-sweep/report",
            JobsApiTestHost.UserToken(Unrelated));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task SystemSweep_SystemTenantCaller_EnqueuesTheAllTenantsJobOwnedByTheSystemTenant()
    {
        using var host = await JobsApiTestHost.StartAsync();
        host.ResetJobClient();

        var response = await host.PostAsync("/system/v1/secrets/sweep?mode=Encrypt",
            JobsApiTestHost.UserToken(SystemTenant));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var job = host.LastEnqueuedJob();
        await Assert.That(job.Method.Name).IsEqualTo(nameof(ISecretSweepJob.RunAllTenants));
        await Assert.That(job.Args[0]).IsEqualTo("OctoSystem");
        await Assert.That(job.Args[1]).IsEqualTo(SecretSweepMode.Encrypt);
        await Assert.That(job.Args[2]).IsEqualTo(SecretSweepTrigger.Manual);
        await Assert.That(job.Args[3]).IsEqualTo(JobsApiTestHost.UserName);
    }

    [Test]
    public async Task SystemSweep_CleanupUnreadable_NeedsConfirm()
    {
        using var host = await JobsApiTestHost.StartAsync();
        host.ResetJobClient();

        var refused = await host.PostAsync("/system/v1/secrets/sweep?mode=CleanupUnreadable",
            JobsApiTestHost.UserToken(SystemTenant));
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(0);

        var accepted = await host.PostAsync("/system/v1/secrets/sweep?mode=CleanupUnreadable&confirm=true",
            JobsApiTestHost.UserToken(SystemTenant));
        await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(host.LastEnqueuedJob().Args[1]).IsEqualTo(SecretSweepMode.CleanupUnreadable);
    }

    [Test]
    public async Task SystemSweep_Encrypt_IsUnchanged_NoConfirmNoRole()
    {
        using var host = await JobsApiTestHost.StartAsync();
        host.ResetJobClient();

        var response = await host.PostAsync("/system/v1/secrets/sweep?mode=Encrypt",
            JobsApiTestHost.UserTokenWithRoles(SystemTenant));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task SystemSweep_OtherTenantCaller_IsForbiddenAndEnqueuesNothing()
    {
        using var host = await JobsApiTestHost.StartAsync();
        host.ResetJobClient();

        var response = await host.PostAsync("/system/v1/secrets/sweep?mode=Encrypt", JobsApiTestHost.UserToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(0);
    }

    [Test]
    public async Task SystemSweep_Decrypt_IsRefused()
    {
        using var host = await JobsApiTestHost.StartAsync();
        host.ResetJobClient();

        var response = await host.PostAsync("/system/v1/secrets/sweep?mode=Decrypt",
            JobsApiTestHost.UserToken(SystemTenant));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(0);
    }

    [Test]
    public async Task SystemReports_SystemTenantCaller_ReturnsAllReports()
    {
        using var host = await JobsApiTestHost.StartAsync();
        host.SecretSweepReportStore.GetAllLastAsync().Returns(new List<SecretSweepReport>
        {
            new() { TenantId = Child, Mode = SecretSweepMode.Verify },
            new() { TenantId = SystemTenant, Mode = SecretSweepMode.Verify }
        });

        var response = await host.GetAsync("/system/v1/secrets/reports", JobsApiTestHost.UserToken(SystemTenant));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(document.RootElement.GetArrayLength()).IsEqualTo(2);
    }

    [Test]
    public async Task SystemReports_OtherTenantCaller_IsForbidden()
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.GetAsync("/system/v1/secrets/reports", JobsApiTestHost.UserToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }
}
