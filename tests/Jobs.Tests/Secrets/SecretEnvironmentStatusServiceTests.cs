using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Runtime.Engine.Secrets;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Secrets;

/// <summary>
///     AB#5544 — <c>GET {tenantId}/v1/secrets/status</c>: key ring, strict mode, Verify schedule, last Verify.
/// </summary>
public class SecretEnvironmentStatusServiceTests
{
    private readonly ISecretAttributeProtector _protector = Substitute.For<ISecretAttributeProtector>();
    private readonly SecretEncryptionOptions _encryption = new();
    private readonly SecretSweepJobOptions _sweep = new();
    private readonly InMemorySecretSweepRunStore _runs = new();
    private readonly DateTimeOffset _now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    private SecretEnvironmentStatusService CreateService()
    {
        return new SecretEnvironmentStatusService(_protector, Options.Create(_encryption), Options.Create(_sweep),
            _runs, new FixedTime(_now));
    }

    [Test]
    public async Task Configured_ReportsKeyIdsOnly_LegacyKey_AndTheVerifySchedule()
    {
        _protector.IsConfigured.Returns(true);
        _protector.ActiveKeyId.Returns("k2");
        _protector.IsKnownKeyId(Arg.Any<string?>()).Returns(true);
        // Obviously fake key material - must never appear in the status.
        _encryption.Keys["k2"] = "FAKE-KEY-MATERIAL-2";
        _encryption.Keys["k1"] = "FAKE-KEY-MATERIAL-1";
        _encryption.LegacyV1Key = "FAKE-LEGACY";
        _protector.IsLegacyV1KeyConfigured.Returns(true);

        var status = await CreateService().GetStatusAsync("t");

        await Assert.That(status.KeyRingConfigured).IsTrue();
        await Assert.That(status.ActiveKeyId).IsEqualTo("k2");
        await Assert.That(status.KnownKeyIds.ToArray()).IsEquivalentTo(new[] { "k1", "k2" });
        await Assert.That(status.KnownKeyIds[0]).IsEqualTo("k1");
        await Assert.That(status.LegacyV1KeyConfigured).IsTrue();
        await Assert.That(status.RecurringVerifyCron).IsEqualTo("0 3 * * *");
        await Assert.That(System.Text.Json.JsonSerializer.Serialize(status)).DoesNotContain("FAKE");
    }

    [Test]
    public async Task NotConfigured_HasNoKeyIds_AndNoActiveKey()
    {
        _protector.IsConfigured.Returns(false);
        _encryption.Keys["k1"] = "FAKE";

        var status = await CreateService().GetStatusAsync("t");

        await Assert.That(status.KeyRingConfigured).IsFalse();
        await Assert.That(status.ActiveKeyId).IsNull();
        await Assert.That(status.KnownKeyIds).IsEmpty();
        await Assert.That(status.LegacyV1KeyConfigured).IsFalse();
        // AB#5534: the missing key ring is a warning the UI shows prominently.
        await Assert.That(status.Warnings).IsEquivalentTo(new List<string> { SecretEnvironmentWarningCodes.NoKeyRing });
    }

    [Test]
    public async Task Configured_WithoutEncV1Findings_HasNoWarnings()
    {
        _protector.IsConfigured.Returns(true);
        _protector.ActiveKeyId.Returns("k1");

        var status = await CreateService().GetStatusAsync("t");

        await Assert.That(status.Warnings).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NoLegacyV1Key_WhenTheLastCompletedRunFoundEncV1(bool countedAsKeyMissing)
    {
        _protector.IsConfigured.Returns(false);
        var totals = new SecretFormCountsReportDto();
        if (countedAsKeyMissing)
        {
            totals.UnknownKeyId = 1;
            totals.UnknownKeyIdByKeyId[SecretValueStates.LegacyV1KeyId] = 1;
        }
        else
        {
            totals.EncV1 = 1;
        }

        await _runs.UpsertAsync("t", new SecretSweepRunDto
        {
            RunId = "1", Mode = SecretSweepModeDto.Verify, Outcome = SecretSweepOutcomeDto.Succeeded,
            CompletedAt = _now.UtcDateTime.AddHours(-1), Totals = totals
        });

        var status = await CreateService().GetStatusAsync("t");
        await Assert.That(status.Warnings).IsEquivalentTo(new List<string>
            { SecretEnvironmentWarningCodes.NoKeyRing, SecretEnvironmentWarningCodes.NoLegacyV1Key });

        // With the legacy key configured there is nothing to warn about for enc:v1.
        _encryption.LegacyV1Key = "FAKE-LEGACY";
        _protector.IsLegacyV1KeyConfigured.Returns(true);
        await Assert.That((await CreateService().GetStatusAsync("t")).Warnings)
            .IsEquivalentTo(new List<string> { SecretEnvironmentWarningCodes.NoKeyRing });
    }

    [Test]
    public async Task NoLegacyV1Key_WhenTheConfiguredLegacyKeyIsRejectedByTheEngine()
    {
        // Set in configuration but not usable (the engine skips an invalid key): enc:v1 values are key missing.
        _protector.IsConfigured.Returns(true);
        _protector.IsLegacyV1KeyConfigured.Returns(false);
        _encryption.LegacyV1Key = "FAKE-INVALID-LEGACY";
        await _runs.UpsertAsync("t", new SecretSweepRunDto
        {
            RunId = "1", Mode = SecretSweepModeDto.Verify, Outcome = SecretSweepOutcomeDto.Succeeded,
            CompletedAt = _now.UtcDateTime.AddHours(-1),
            Totals = new SecretFormCountsReportDto
            {
                UnknownKeyId = 1,
                UnknownKeyIdByKeyId = { [SecretValueStates.LegacyV1KeyId] = 1 }
            }
        });

        var status = await CreateService().GetStatusAsync("t");

        await Assert.That(status.LegacyV1KeyConfigured).IsFalse();
        await Assert.That(status.Warnings)
            .IsEquivalentTo(new List<string> { SecretEnvironmentWarningCodes.NoLegacyV1Key });
        await Assert.That(System.Text.Json.JsonSerializer.Serialize(status)).DoesNotContain("FAKE");
    }

    [Test]
    public async Task NoLegacyV1Key_FollowsTheLatestCompletedRunOnly()
    {
        _protector.IsConfigured.Returns(true);
        await _runs.UpsertAsync("t", new SecretSweepRunDto
        {
            RunId = "old", Mode = SecretSweepModeDto.Verify, Outcome = SecretSweepOutcomeDto.Succeeded,
            CompletedAt = _now.UtcDateTime.AddDays(-2), Totals = new SecretFormCountsReportDto { EncV1 = 3 }
        });
        await _runs.UpsertAsync("t", new SecretSweepRunDto
        {
            RunId = "new", Mode = SecretSweepModeDto.Verify, Outcome = SecretSweepOutcomeDto.Succeeded,
            CompletedAt = _now.UtcDateTime.AddDays(-1), Totals = new SecretFormCountsReportDto { EncV2 = 3 }
        });

        await Assert.That((await CreateService().GetStatusAsync("t")).Warnings).IsEmpty();
    }

    [Test]
    public async Task VerifyCronEmpty_IsNull()
    {
        _sweep.VerifyCron = " ";

        await Assert.That((await CreateService().GetStatusAsync("t")).RecurringVerifyCron).IsNull();
    }

    [Test]
    public async Task StrictMode_FromTheBotDate_OrTheEngineFlag()
    {
        _sweep.StrictModeSince = _now.AddDays(1);
        var scheduled = await CreateService().GetStatusAsync("t");
        await Assert.That(scheduled.StrictMode).IsFalse();
        await Assert.That(scheduled.StrictModeSince).IsEqualTo(_now.AddDays(1).UtcDateTime);

        _sweep.StrictModeSince = _now.AddDays(-1);
        await Assert.That((await CreateService().GetStatusAsync("t")).StrictMode).IsTrue();

        _sweep.StrictModeSince = null;
        _protector.IsStrictMode.Returns(true);
        var engine = await CreateService().GetStatusAsync("t");
        await Assert.That(engine.StrictMode).IsTrue();
        await Assert.That(engine.StrictModeSince).IsNull();
    }

    [Test]
    public async Task LastVerifyAt_IsTheLatestCompletedVerifyRunOfTheTenant()
    {
        var older = new DateTime(2026, 10, 5, 3, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 10, 6, 3, 0, 0, DateTimeKind.Utc);
        await _runs.UpsertAsync("t", new SecretSweepRunDto
            { RunId = "1", Mode = SecretSweepModeDto.Verify, Outcome = SecretSweepOutcomeDto.Succeeded, CompletedAt = older });
        await _runs.UpsertAsync("t", new SecretSweepRunDto
        {
            RunId = "2", Mode = SecretSweepModeDto.Verify, Outcome = SecretSweepOutcomeDto.CompletedWithFailures,
            CompletedAt = newer
        });
        await _runs.UpsertAsync("t", new SecretSweepRunDto
        {
            RunId = "3", Mode = SecretSweepModeDto.Verify, Outcome = SecretSweepOutcomeDto.Skipped,
            CompletedAt = newer.AddHours(1)
        });
        await _runs.UpsertAsync("t", new SecretSweepRunDto
            { RunId = "4", Mode = SecretSweepModeDto.Encrypt, Outcome = SecretSweepOutcomeDto.Succeeded, CompletedAt = newer.AddHours(2) });
        await _runs.UpsertAsync("other", new SecretSweepRunDto
            { RunId = "5", Mode = SecretSweepModeDto.Verify, Outcome = SecretSweepOutcomeDto.Succeeded, CompletedAt = newer.AddDays(1) });

        await Assert.That((await CreateService().GetStatusAsync("t")).LastVerifyAt).IsEqualTo(newer);
        await Assert.That((await CreateService().GetStatusAsync("none")).LastVerifyAt).IsNull();
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
