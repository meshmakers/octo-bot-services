using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.Jobs.Commands;

/// <summary>
///     Interface for exporting a runtime model by query to a file
/// </summary>
public interface IExportRtModelByQueryCommand
{
    /// <summary>
    ///     Exports a runtime model by query to a file.
    /// </summary>
    /// <param name="tenantId">The corresponding tenant id</param>
    /// <param name="queryId">ID of query, whose data is exported</param>
    /// <param name="filePath">The file path to export to</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns></returns>
    Task ExportAsync(string tenantId, OctoObjectId queryId, string filePath,
        CancellationToken? cancellationToken);
}