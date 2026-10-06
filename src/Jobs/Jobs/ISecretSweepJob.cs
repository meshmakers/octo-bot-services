using System.ComponentModel;
using Hangfire;
using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Runtime.Contracts.Secrets;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
///     Secret sweep job (AB#5539, concept AB#5528 §5.2 phase 4, §5.3): <c>Encrypt</c> once per environment
///     (clear text and <c>enc:v1</c> become <c>enc:v2</c> with the active key), then a recurring
///     <c>Verify</c>. Every writing run starts with a fresh pre-sweep dump of the tenant (decision 10).
/// </summary>
public interface ISecretSweepJob
{
    /// <summary>
    ///     Sweeps one tenant. Fails (Hangfire state Failed) when the sweep was skipped or failed; the report
    ///     is stored either way (<c>GET {tenantId}/v1/jobs/secret-sweep/report</c>) and is the job result on
    ///     success.
    /// </summary>
    /// <param name="tenantId">The tenant to sweep</param>
    /// <param name="mode">Verify, Encrypt, Reprotect or ClearUnknownKid (Decrypt is refused)</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns>The tenant's report</returns>
    [DisplayName("Secret sweep {1} of tenant '{0}'")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(60 * 60)]
    Task<SecretSweepReport> Run(string tenantId, SecretSweepMode mode, IBotCancellationToken? cancellationToken);

    /// <summary>
    ///     Sweeps every tenant of the instance: the system tenant and every registered tenant, child tenants
    ///     included. One tenant's problem does not stop the others; the job fails at the end when any tenant
    ///     was skipped or failed.
    /// </summary>
    /// <param name="tenantId">
    ///     The system tenant, which owns the instance-wide run. Named <c>tenantId</c> on purpose: the job
    ///     instance endpoints bind a job to the tenant in this argument (AB#5070), so only callers of the
    ///     system tenant can read or delete the run.
    /// </param>
    /// <param name="mode">Verify, Encrypt, Reprotect or ClearUnknownKid (Decrypt is refused)</param>
    /// <param name="trigger">Recorded in the reports (recurring job or on demand)</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns>A summary with one report per tenant</returns>
    [DisplayName("Secret sweep {1} of all tenants")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(60 * 60 * 6)]
    Task<SecretSweepRunSummary> RunAllTenants(string tenantId, SecretSweepMode mode, SecretSweepTrigger trigger,
        IBotCancellationToken? cancellationToken);
}
