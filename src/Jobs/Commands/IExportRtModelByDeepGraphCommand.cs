using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

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
    /// <param name="securityContext">
    ///     The requesting caller (AB#6734); the export reads with this caller's data permissions. The system
    ///     context is refused.
    /// </param>
    /// <param name="filePath">The file path to export to</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns></returns>
    Task ExportAsync(string tenantId, ExportRtByDeepGraphCommandRequest rtByDeepGraphCommandRequest,
        RtSecurityContext securityContext, string filePath,
        CancellationToken? cancellationToken);
}