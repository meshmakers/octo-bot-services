using Hangfire;

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
    [AutomaticRetry(Attempts = 0)]
    Task ImportCkAsync(string tenantId, string key,
        IBotCancellationToken? cancellationToken);

    /// <summary>
    ///     Imports a runtime model
    /// </summary>
    /// <param name="tenantId">The corresponding tenant</param>
    /// <param name="key">The key definition in redis</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns></returns>
    [JobDisplayName("Importing Runtime Metadata to tenant '{0}'")]
    [AutomaticRetry(Attempts = 0)]
    Task ImportRtAsync(string tenantId, string key, IBotCancellationToken? cancellationToken);
}