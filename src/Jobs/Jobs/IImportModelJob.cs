using Hangfire;
using Meshmakers.Octo.Runtime.Contracts.Exchange;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
///     Imports a construction kit model or a runtime object graph
/// </summary>
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
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
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
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
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
    [JobDisplayName("Importing Runtime Metadata to tenant '{0}'")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    Task ImportRtAsync(string tenantId, ImportStrategy importStrategy, string key, IBotCancellationToken? cancellationToken);
}