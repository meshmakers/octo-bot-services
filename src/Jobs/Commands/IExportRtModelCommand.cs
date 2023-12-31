using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.Jobs.Commands;

/// <summary>
/// Interface for exporting a runtime model to a file.
/// </summary>
public interface IExportRtModelCommand
{
    /// <summary>
    /// Exports as file
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="queryId"></param>
    /// <param name="filePath"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task ExportAsync(string tenantId, OctoObjectId queryId, string filePath,
        CancellationToken? cancellationToken);
}