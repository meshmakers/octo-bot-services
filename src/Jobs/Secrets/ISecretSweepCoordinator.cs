using Meshmakers.Octo.Runtime.Contracts.Secrets;

namespace Meshmakers.Octo.Backend.Jobs.Secrets;

/// <summary>
///     Bot-side orchestration of the secret sweep (AB#5539, concept §5.2 phases 4-5, §5.3, §6) on top of the
///     engine's <see cref="ISecretMaintenanceService" />: key check, pre-sweep dump, the sweep, a verify of
///     the final state, strict-mode evaluation, report persistence and logging.
/// </summary>
public interface ISecretSweepCoordinator
{
    /// <summary>
    ///     The tenants an instance-wide sweep covers: the system tenant first, then every registered tenant
    ///     (child tenants included). Empty when the system tenant does not exist.
    /// </summary>
    Task<IReadOnlyList<string>> GetTenantIdsAsync();

    /// <summary>
    ///     Sweeps one tenant. Never throws for tenant-level problems - they are reported in the returned
    ///     report (<see cref="SecretSweepOutcome.Skipped" /> / <see cref="SecretSweepOutcome.Failed" />).
    ///     <see cref="SecretSweepMode.Decrypt" /> is refused (emergency only, not available through the
    ///     bot).
    /// </summary>
    /// <param name="tenantId">Tenant</param>
    /// <param name="mode">
    ///     Verify, Encrypt, Reprotect or CleanupUnreadable (callers confirm CleanupUnreadable before enqueuing)
    /// </param>
    /// <param name="trigger">What started the sweep</param>
    /// <param name="runInfo">Run id and starter for the run history</param>
    /// <param name="cancellationToken">Cancellation</param>
    Task<SecretSweepReport> SweepTenantAsync(string tenantId, SecretSweepMode mode, SecretSweepTrigger trigger,
        SecretSweepRunInfo? runInfo, CancellationToken cancellationToken);

    /// <summary>
    ///     Post-restore handling (concept §6, decisions 2026-10-06 item 2): <c>Verify</c>, <c>Encrypt</c> (older
    ///     plaintext / <c>enc:v1</c> dumps), <c>Verify</c>. Nothing is deleted: values whose key id is unknown
    ///     here stay encrypted and are reported in <see cref="SecretSweepReport.Unreadable" /> and
    ///     <see cref="SecretSweepReport.SecretsToReEnter" />. Without a key ring on the bot (AB#5539) only a
    ///     key-free <c>Verify</c> runs: it classifies by key id (every <c>enc:v2</c> whose key id is not in the
    ///     empty ring and every <c>enc:v1</c> without legacy key counts as key missing and is listed for
    ///     re-entry), writes nothing, and the run (mode <c>Verify</c>) ends <see cref="SecretSweepOutcome.Succeeded" />
    ///     with the reason "No key ring configured: secrets were classified only; set the key ring and run
    ///     Encrypt" (<see cref="SecretSweepOutcome.CompletedWithFailures" /> when the Verify reported failures).
    ///     Skipped when <c>Bot:SecretSweep:RunAfterRestore</c> is off. Never throws for tenant-level problems.
    /// </summary>
    /// <param name="tenantId">Restored tenant</param>
    /// <param name="runInfo">Run id (the restore job) for the run history</param>
    /// <param name="cancellationToken">Cancellation</param>
    Task<SecretSweepReport> RunAfterRestoreAsync(string tenantId, SecretSweepRunInfo? runInfo,
        CancellationToken cancellationToken);
}
