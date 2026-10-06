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
    /// <param name="mode">Verify, Encrypt, Reprotect or ClearUnknownKid</param>
    /// <param name="trigger">What started the sweep</param>
    /// <param name="cancellationToken">Cancellation</param>
    Task<SecretSweepReport> SweepTenantAsync(string tenantId, SecretSweepMode mode, SecretSweepTrigger trigger,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Post-restore handling (concept §6, decision 5): <c>ClearUnknownKid</c> (values from another
    ///     environment become "not set" and are listed in <see cref="SecretSweepReport.SecretsToReEnter" />),
    ///     then <c>Encrypt</c> (older plaintext dumps), then <c>Verify</c>. Skipped when no key is configured or
    ///     <c>Bot:SecretSweep:RunAfterRestore</c> is off. Never throws for tenant-level problems.
    /// </summary>
    /// <param name="tenantId">Restored tenant</param>
    /// <param name="cancellationToken">Cancellation</param>
    Task<SecretSweepReport> RunAfterRestoreAsync(string tenantId, CancellationToken cancellationToken);
}
