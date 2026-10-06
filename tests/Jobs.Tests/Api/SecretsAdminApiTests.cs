using System.Net;
using System.Text.Json;
using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Api;

/// <summary>
///     AB#5544 — the secrets admin endpoints of the tenant route <c>{tenantId}/v1/secrets/...</c> (contract §9):
///     status, sweep runs, early dump deletion, through the real request pipeline.
/// </summary>
internal class SecretsAdminApiTests
{
    private const string Parent = JobsApiTestHost.Parent;
    private const string Child = JobsApiTestHost.Child;
    private const string Unrelated = JobsApiTestHost.Unrelated;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task Status_AnyUserOfTheTenant_WithoutRoles_GetsTheStatusInContractShape()
    {
        using var host = await JobsApiTestHost.StartAsync();
        host.SecretEnvironmentStatusService.GetStatusAsync(Child).Returns(new BotSecretEnvironmentStatusDto
        {
            KeyRingConfigured = true,
            ActiveKeyId = "k1",
            KnownKeyIds = ["k1", "k2"],
            LegacyV1KeyConfigured = true,
            StrictMode = false,
            StrictModeSince = null,
            RecurringVerifyCron = "0 3 * * *",
            LastVerifyAt = new DateTime(2026, 10, 6, 3, 0, 12, DateTimeKind.Utc),
            Warnings = [SecretEnvironmentWarningCodes.NoLegacyV1Key]
        });

        var response = await host.GetAsync($"/{Child}/v1/secrets/status", JobsApiTestHost.UserTokenWithRoles(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        await Assert.That(root.GetProperty("keyRingConfigured").GetBoolean()).IsTrue();
        await Assert.That(root.GetProperty("activeKeyId").GetString()).IsEqualTo("k1");
        await Assert.That(root.GetProperty("knownKeyIds").GetArrayLength()).IsEqualTo(2);
        await Assert.That(root.GetProperty("legacyV1KeyConfigured").GetBoolean()).IsTrue();
        await Assert.That(root.GetProperty("strictMode").GetBoolean()).IsFalse();
        await Assert.That(root.GetProperty("strictModeSince").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(root.GetProperty("recurringVerifyCron").GetString()).IsEqualTo("0 3 * * *");
        await Assert.That(root.GetProperty("lastVerifyAt").GetDateTime())
            .IsEqualTo(new DateTime(2026, 10, 6, 3, 0, 12, DateTimeKind.Utc));
        // AB#5534: warning codes as a camelCase string array.
        await Assert.That(root.GetProperty("warnings").GetArrayLength()).IsEqualTo(1);
        await Assert.That(root.GetProperty("warnings")[0].GetString()).IsEqualTo("NoLegacyV1Key");
    }

    [Test]
    public async Task Status_UnrelatedTenant_IsForbidden()
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.GetAsync($"/{Child}/v1/secrets/status", JobsApiTestHost.UserToken(Unrelated));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task SecretsRoutes_DoNotOpenTheParentAdministrationPath()
    {
        // The parent-tenant administration marker is limited to the job routes.
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.GetAsync($"/{Child}/v1/secrets/sweep-runs", JobsApiTestHost.UserToken(Parent));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task SweepRuns_AdminPanelManagement_GetsTheRunsInContractShape_DefaultLimit20()
    {
        using var host = await JobsApiTestHost.StartAsync();
        var started = new DateTime(2026, 10, 6, 10, 0, 0, DateTimeKind.Utc);
        host.SecretSweepRunService.GetRunsAsync(Child, Arg.Any<int>()).Returns(new List<SecretSweepRunDto>
        {
            new()
            {
                RunId = "job-2",
                Mode = SecretSweepModeDto.CleanupUnreadable,
                Trigger = SecretSweepTriggerDto.Manual,
                Outcome = SecretSweepOutcomeDto.Running,
                StartedAt = started,
                TriggeredBy = "alice",
                Totals = new SecretFormCountsReportDto { EncV2 = 3, Total = 3 },
                PlaceholdersNormalized = 1,
                UnreadableCount = 2,
                Dump = new SecretSweepDumpDto
                {
                    FileName = "childtenant-20261006-100000-abcd1234.presweep.tar.gz",
                    Exists = true,
                    SizeBytes = 123,
                    CreatedAt = started,
                    ExpiresAt = started.AddDays(7)
                }
            },
            new()
            {
                RunId = "job-1", Mode = SecretSweepModeDto.Verify, Trigger = SecretSweepTriggerDto.Recurring,
                Outcome = SecretSweepOutcomeDto.Succeeded, StartedAt = started.AddDays(-1),
                CompletedAt = started.AddDays(-1)
            }
        });

        var response = await host.GetAsync($"/{Child}/v1/secrets/sweep-runs",
            JobsApiTestHost.UserTokenWithRoles(Child, CommonConstants.AdminPanelManagementRole));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await host.SecretSweepRunService.Received(1).GetRunsAsync(Child, 20);
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var first = document.RootElement[0];
        await Assert.That(first.GetProperty("runId").GetString()).IsEqualTo("job-2");
        await Assert.That(first.GetProperty("mode").GetString()).IsEqualTo("CleanupUnreadable");
        await Assert.That(first.GetProperty("trigger").GetString()).IsEqualTo("Manual");
        await Assert.That(first.GetProperty("outcome").GetString()).IsEqualTo("Running");
        await Assert.That(first.GetProperty("completedAt").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(first.GetProperty("triggeredBy").GetString()).IsEqualTo("alice");
        await Assert.That(first.GetProperty("totals").GetProperty("encV2").GetInt64()).IsEqualTo(3);
        await Assert.That(first.GetProperty("placeholdersNormalized").GetInt64()).IsEqualTo(1);
        await Assert.That(first.GetProperty("unreadableCount").GetInt64()).IsEqualTo(2);
        var dump = first.GetProperty("dump");
        await Assert.That(dump.GetProperty("fileName").GetString())
            .IsEqualTo("childtenant-20261006-100000-abcd1234.presweep.tar.gz");
        await Assert.That(dump.GetProperty("exists").GetBoolean()).IsTrue();
        await Assert.That(dump.GetProperty("sizeBytes").GetInt64()).IsEqualTo(123);
        await Assert.That(dump.GetProperty("expiresAt").GetDateTime()).IsEqualTo(started.AddDays(7));
        await Assert.That(dump.GetProperty("deletedAt").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(document.RootElement[1].GetProperty("dump").ValueKind).IsEqualTo(JsonValueKind.Null);

        // The SDK reads the same JSON.
        var runs = JsonSerializer.Deserialize<List<SecretSweepRunDto>>(json, Web)!;
        await Assert.That(runs[0].Outcome).IsEqualTo(SecretSweepOutcomeDto.Running);
        await Assert.That(runs[0].Dump!.SizeBytes).IsEqualTo(123);
    }

    [Test]
    [Arguments(1, 1)]
    [Arguments(50, 50)]
    [Arguments(500, 50)]
    public async Task SweepRuns_Limit_IsPassedAndCappedAt50(int limit, int expected)
    {
        using var host = await JobsApiTestHost.StartAsync();
        host.SecretSweepRunService.GetRunsAsync(Child, Arg.Any<int>()).Returns(new List<SecretSweepRunDto>());

        var response = await host.GetAsync($"/{Child}/v1/secrets/sweep-runs?limit={limit}",
            JobsApiTestHost.UserToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await host.SecretSweepRunService.Received(1).GetRunsAsync(Child, expected);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task SweepRuns_LimitBelowOne_Is400(int limit)
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.GetAsync($"/{Child}/v1/secrets/sweep-runs?limit={limit}",
            JobsApiTestHost.UserToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task SweepRuns_WithoutAdminPanelManagement_IsForbidden()
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.GetAsync($"/{Child}/v1/secrets/sweep-runs",
            JobsApiTestHost.UserTokenWithRoles(Child, CommonConstants.SecretManagementRole));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await host.SecretSweepRunService.DidNotReceiveWithAnyArgs().GetRunsAsync(default!, default);
    }

    [Test]
    [Arguments(SecretSweepDumpDeleteResultDto.Deleted, HttpStatusCode.NoContent)]
    [Arguments(SecretSweepDumpDeleteResultDto.NotFound, HttpStatusCode.NotFound)]
    [Arguments(SecretSweepDumpDeleteResultDto.AlreadyDeleted, HttpStatusCode.Conflict)]
    public async Task DeleteDump_SecretManagement_MapsTheResult_AndRecordsTheUser(
        SecretSweepDumpDeleteResultDto result, HttpStatusCode expected)
    {
        using var host = await JobsApiTestHost.StartAsync();
        host.SecretSweepRunService.DeleteDumpAsync(Child, "job-2", Arg.Any<string?>()).Returns(result);

        var response = await host.DeleteAsync($"/{Child}/v1/secrets/sweep-runs/job-2/dump",
            JobsApiTestHost.UserTokenWithRoles(Child, CommonConstants.SecretManagementRole));

        await Assert.That(response.StatusCode).IsEqualTo(expected);
        await host.SecretSweepRunService.Received(1).DeleteDumpAsync(Child, "job-2", JobsApiTestHost.UserName);
    }

    [Test]
    public async Task DeleteDump_WithoutSecretManagement_IsForbidden_AndDeletesNothing()
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.DeleteAsync($"/{Child}/v1/secrets/sweep-runs/job-2/dump",
            JobsApiTestHost.UserTokenWithRoles(Child, CommonConstants.AdminPanelManagementRole));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await host.SecretSweepRunService.DidNotReceiveWithAnyArgs().DeleteDumpAsync(default!, default!, default);
    }

    [Test]
    public async Task Dump_HasNoDownloadRoute()
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.GetAsync($"/{Child}/v1/secrets/sweep-runs/job-2/dump",
            JobsApiTestHost.UserToken(Child));

        await Assert.That(response.IsSuccessStatusCode).IsFalse();
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.OK);
    }
}
