using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.Jobs.Commands;

/// <summary>
///     Interface for exporting a runtime model by deep graph to a file
/// </summary>
public interface IExportRtModelByDeepGraphCommand
{
    /// <summary>
    ///     Exports a runtime model by deep graph to a file.
    /// </summary>
    /// <param name="tenantId">The corresponding tenant id</param>
    /// <param name="originRtIds">The origin runtime ids</param>
    /// <param name="originCkTypeId">The origin construction kit type id</param>
    /// <param name="filePath">The file path to export to</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns></returns>
    Task ExportAsync(string tenantId, IEnumerable<OctoObjectId> originRtIds, CkId<CkTypeId> originCkTypeId,
        string filePath,
        CancellationToken? cancellationToken);
}