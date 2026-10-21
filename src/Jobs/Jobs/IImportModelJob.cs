using Hangfire;
using Meshmakers.Octo.Runtime.Contracts.Exchange;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
///     Imports a construction kit model or a runtime object graph
/// </summary>
/// <remarks>
///     Retry policy (AB#7256): a failed job is never deleted, it stays <c>Failed</c> in Hangfire for diagnosis. CK
///     imports are retried three times (after 10, 30 and 60 seconds): importing the same CK model version again is
///     safe, and the cache streams are cleared only after a successful import. Runtime imports are not retried by
///     Hangfire because their import strategies are not idempotent; they only repeat the cache lookup in-process.
/// </remarks>
public interface IImportModelJob
{
    /// <summary>
    ///     Imports a CK model
    /// </summary>
    /// <param name="tenantId">The corresponding tenant id</param>
    /// <param name="key">The key definition in redis</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns></returns>
    [JobDisplayName("Importing ConstructionKit Metadata to tenant '{0}'")]
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [10, 30, 60], OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    Task ImportCkAsync(string tenantId, string key,
        IBotCancellationToken? cancellationToken);

    /// <summary>
    ///     Imports multiple CK models sequentially in dependency order.
    ///     This prevents race conditions where parallel imports fail because
    ///     dependencies are still in "Importing" state.
    /// </summary>
    /// <param name="tenantId">The corresponding tenant id</param>
    /// <param name="keys">Ordered list of cache keys (one per model, dependencies first)</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    [JobDisplayName("Importing ConstructionKit Metadata batch to tenant '{0}'")]
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [10, 30, 60], OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    Task ImportCkBatchAsync(string tenantId, List<string> keys,
        IBotCancellationToken? cancellationToken);

    /// <summary>
    ///     Imports a runtime model
    /// </summary>
    /// <param name="tenantId">The corresponding tenant</param>
    /// <param name="importStrategy">The import strategy to use</param>
    /// <param name="key">The key definition in redis</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns></returns>
    /// <remarks>
    ///     Kept for jobs that were enqueued before the initiating caller was passed along (AB#6392). It runs the
    ///     import with the blueprint-lock protection and an unknown caller - never as a system import.
    /// </remarks>
    [JobDisplayName("Importing Runtime Metadata to tenant '{0}'")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    Task ImportRtAsync(string tenantId, ImportStrategy importStrategy, string key, IBotCancellationToken? cancellationToken);

    /// <summary>
    ///     Imports a runtime model on behalf of the caller that started the import through the API (AB#6392). The
    ///     import applies the blueprint-lock protection of CK types whose data policy opted in
    ///     (<c>ProtectBlueprintLocked</c>): it fails atomically, listing every locked entity, before anything is
    ///     written, and strips the blueprint bookkeeping attributes of the file.
    /// </summary>
    /// <param name="tenantId">The corresponding tenant</param>
    /// <param name="importStrategy">The import strategy to use</param>
    /// <param name="key">The key definition in redis</param>
    /// <param name="initiatedBySubjectId">Subject id of the caller; a missing one is still protected, never system</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns></returns>
    [JobDisplayName("Importing Runtime Metadata to tenant '{0}'")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    Task ImportRtAsync(string tenantId, ImportStrategy importStrategy, string key, string? initiatedBySubjectId,
        IBotCancellationToken? cancellationToken);
}