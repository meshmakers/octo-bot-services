namespace Meshmakers.Octo.Backend.Jobs.Secrets;

/// <summary>
///     Mutual exclusion of secret sweeps per tenant (AB#5539). Hangfire's
///     <c>DisableConcurrentExecution</c> locks per job <i>method</i>, so a single-tenant run, the
///     all-tenants run and the post-restore sweep could otherwise work on the same tenant at the same time
///     (two pre-sweep dumps, interleaved reports, a ClearUnknownKid racing an Encrypt).
/// </summary>
public interface ISecretSweepTenantLock
{
    /// <summary>
    ///     Tries to acquire the sweep lock of <paramref name="tenantId" />, waiting at most
    ///     <paramref name="timeout" />.
    /// </summary>
    /// <returns>The held lock (dispose to release), or <c>null</c> when another sweep holds it.</returns>
    IDisposable? TryAcquire(string tenantId, TimeSpan timeout);
}
