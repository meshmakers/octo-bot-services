using System.Net;
using System.Text.Json;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Secrets;
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
    [Arguments("Encrypt", SecretSweepMode.Encrypt)]
    [Arguments("Reprotect", SecretSweepMode.Reprotect)]
    [Arguments("ClearUnknownKid", SecretSweepMode.ClearUnknownKid)]
    [Arguments("1", SecretSweepMode.Encrypt)]
    public async Task TenantSweep_EnqueuesTheSweepJobWithTheMode(string modeQuery, SecretSweepMode expected)
    {
        using var host = await JobsApiTestHost.StartAsync();
        host.ResetJobClient();

        var response = await host.PostAsync($"/{Child}/v1/jobs/secret-sweep?mode={modeQuery}",
            JobsApiTestHost.UserToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var job = host.LastEnqueuedJob();
        await Assert.That(job.Type).IsEqualTo(typeof(ISecretSweepJob));
        await Assert.That(job.Method.Name).IsEqualTo(nameof(ISecretSweepJob.Run));
        await Assert.That(job.Args[0]).IsEqualTo(Child);
        await Assert.That(job.Args[1]).IsEqualTo(expected);
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

        var response = await host.PostAsync($"/{Child}/v1/jobs/secret-sweep?mode=Decrypt",
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
