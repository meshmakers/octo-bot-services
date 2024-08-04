using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;

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
    /// <param name="rtByDeepGraphCommandRequest">The command request</param>
    /// <param name="filePath">The file path to export to</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns></returns>
    Task ExportAsync(string tenantId, ExportRtByDeepGraphCommandRequest rtByDeepGraphCommandRequest,
        string filePath,
        CancellationToken? cancellationToken);
}