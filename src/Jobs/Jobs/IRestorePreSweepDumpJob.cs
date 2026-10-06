using System.ComponentModel;
using Hangfire;
using Hangfire.Server;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
///     Restores the pre-sweep dump of a secret sweep run into the same tenant (AB#5559, concept
///     <c>secret-sweep-dump-storage.md</c> Q1). Admin operation: the API requires the tenant role
///     <c>SecretManagement</c> and <c>confirm=true</c>, and resolves the run in the route tenant's own run history,
///     so a dump can only ever be restored into the tenant it was taken from.
/// </summary>
/// <remarks>
///     🔴 A dump taken before the first <c>Encrypt</c> sweep holds <b>plaintext</b> secrets: restoring it brings the
///     plaintext back. The job runs a <c>Verify</c> afterwards and reports the counts; run <c>Encrypt</c> again.
/// </remarks>
public interface IRestorePreSweepDumpJob
{
    /// <summary>
    ///     Decrypts the run's dump from the artifact store into the local scratch directory, restores it with
    ///     mongorestore (the same mechanism as the repository restore: the tenant's database is dropped and
    ///     replaced), deletes the scratch file and runs a <c>Verify</c> sweep.
    /// </summary>
    /// <param name="tenantId">The tenant (route tenant) whose run history holds <paramref name="runId" /></param>
    /// <param name="runId">The sweep run whose pre-sweep dump is restored</param>
    /// <param name="triggeredBy">User name of whoever started the restore, or <c>null</c></param>
    /// <param name="performContext">Supplied by Hangfire (pass <c>null</c>); its job id is the run id of the Verify</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns>The restore result with the Verify report; <c>null</c> when the system tenant does not exist</returns>
    [DisplayName("Restore pre-sweep dump of run '{1}' into tenant '{0}'")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(60 * 10)]
    Task<PreSweepDumpRestoreResult?> Run(string tenantId, string runId, string? triggeredBy,
        PerformContext? performContext, IBotCancellationToken? cancellationToken);
}
