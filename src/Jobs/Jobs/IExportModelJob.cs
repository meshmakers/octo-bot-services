using Hangfire;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
///     HangFire Job that implements the export of CK and RT model files
/// </summary>
public interface IExportModelJob
{
    /// <summary>
    ///     Exports a runtime model by query to a file.
    /// </summary>
    /// <param name="tenantId">The corresponding tenant id</param>
    /// <param name="queryId">ID of query, whose data is exported</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns>The key the result file is stored.</returns>
    [JobDisplayName("Export Runtime Model by query '{1}' from tenant '{0}'")]
    Task<string> ExportRtModelByQueryAsync(string tenantId, OctoObjectId queryId,
        IBotCancellationToken? cancellationToken);

    /// <summary>
    ///     Exports a runtime model by deep graph to a file.
    /// </summary>
    /// <param name="tenantId">The corresponding tenant id</param>
    /// <param name="originRtIds">The origin runtime ids</param>
    /// <param name="originCkTypeId">The origin CK type id</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns>The key the result file is stored.</returns>
    [JobDisplayName("Export Runtime Model by deep graph from tenant '{0}'")]
    Task<string> ExportRtModelByDeepGraphAsync(string tenantId, IEnumerable<OctoObjectId> originRtIds,
        CkId<CkTypeId> originCkTypeId,
        IBotCancellationToken? cancellationToken);
}