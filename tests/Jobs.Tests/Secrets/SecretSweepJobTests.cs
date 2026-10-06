using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Secrets;

/// <summary>
///     AB#5539 — the Hangfire job around the coordinator.
/// </summary>
public class SecretSweepJobTests
{
    private readonly ISecretSweepCoordinator _coordinator = Substitute.For<ISecretSweepCoordinator>();

    private SecretSweepJob CreateJob()
    {
        return new SecretSweepJob(Substitute.For<ILogger<SecretSweepJob>>(), _coordinator);
    }

    private static SecretSweepReport Report(string tenantId, SecretSweepOutcome outcome, long remaining = 0)
    {
        return new SecretSweepReport
        {
            TenantId = tenantId, Outcome = outcome, Reason = outcome == SecretSweepOutcome.Skipped ? "no dump" : null,
            RemainingLegacyValues = remaining
        };
    }

    [Test]
    [Arguments(SecretSweepOutcome.Succeeded)]
    [Arguments(SecretSweepOutcome.CompletedWithFailures)]
    public async Task Run_Completed_ReturnsTheReport(SecretSweepOutcome outcome)
    {
        var report = Report("t1", outcome);
        _coordinator.SweepTenantAsync("t1", SecretSweepMode.Encrypt, SecretSweepTrigger.Manual,
            Arg.Any<CancellationToken>()).Returns(report);

        var result = await CreateJob().Run("t1", SecretSweepMode.Encrypt, null);

        await Assert.That(result).IsSameReferenceAs(report);
    }

    [Test]
    [Arguments(SecretSweepOutcome.Skipped)]
    [Arguments(SecretSweepOutcome.Failed)]
    public async Task Run_SkippedOrFailed_FailsTheJob(SecretSweepOutcome outcome)
    {
        _coordinator.SweepTenantAsync("t1", SecretSweepMode.Encrypt, SecretSweepTrigger.Manual,
            Arg.Any<CancellationToken>()).Returns(Report("t1", outcome));

        await Assert.That(async () => await CreateJob().Run("t1", SecretSweepMode.Encrypt, null))
            .Throws<JobFailedException>();
    }

    [Test]
    public async Task RunAllTenants_SweepsEveryTenant_WithTheTrigger()
    {
        _coordinator.GetTenantIdsAsync().Returns(new[] { "octosystem", "parent", "child" });
        _coordinator.SweepTenantAsync(Arg.Any<string>(), SecretSweepMode.Verify, SecretSweepTrigger.Recurring,
                Arg.Any<CancellationToken>())
            .Returns(ci => Report(ci.Arg<string>(), SecretSweepOutcome.Succeeded, 1));

        var summary = await CreateJob().RunAllTenants("octosystem", SecretSweepMode.Verify,
            SecretSweepTrigger.Recurring, null);

        await Assert.That(summary.Tenants.Select(t => t.TenantId).ToArray())
            .IsEquivalentTo(new[] { "octosystem", "parent", "child" });
        await Assert.That(summary.Count(SecretSweepOutcome.Succeeded)).IsEqualTo(3);
        await Assert.That(summary.Mode).IsEqualTo(SecretSweepMode.Verify);
        await Assert.That(summary.Trigger).IsEqualTo(SecretSweepTrigger.Recurring);
    }

    [Test]
    public async Task RunAllTenants_OneTenantSkipped_StillSweepsTheOthers_ThenFails()
    {
        _coordinator.GetTenantIdsAsync().Returns(new[] { "octosystem", "broken", "child" });
        _coordinator.SweepTenantAsync(Arg.Any<string>(), SecretSweepMode.Encrypt, SecretSweepTrigger.Manual,
                Arg.Any<CancellationToken>())
            .Returns(ci => Report(ci.Arg<string>(),
                ci.Arg<string>() == "broken" ? SecretSweepOutcome.Skipped : SecretSweepOutcome.Succeeded));

        await Assert.That(async () => await CreateJob().RunAllTenants("octosystem", SecretSweepMode.Encrypt,
                SecretSweepTrigger.Manual, null))
            .Throws<JobFailedException>().WithMessageContaining("broken (Skipped)");

        await _coordinator.Received(1).SweepTenantAsync("child", SecretSweepMode.Encrypt, SecretSweepTrigger.Manual,
            Arg.Any<CancellationToken>());
    }
}
