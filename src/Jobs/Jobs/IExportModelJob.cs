using Hangfire;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
///     HangFire Job that implements the export of CK and RT model files
/// </summary>
public interface IExportModelJob
{
    /// <summary>
    ///     Exports a runtime model by query to a file.
    /// </summary>
    /// <param name="rtByQueryCommandRequest">The command request</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns>The key the result file is stored.</returns>
    [JobDisplayName("Export Runtime Model by query '{1}' from tenant '{0}'")]
    [AutomaticRetry(Attempts = 0)]
    Task<string> ExportRtModelByQueryAsync(ExportRtByQueryCommandRequest rtByQueryCommandRequest,
        IBotCancellationToken? cancellationToken);

    /// <summary>
    ///     Exports a runtime model by deep graph to a file.
    /// </summary>
    /// <param name="rtByDeepGraphCommandRequest">The command request</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns>The key the result file is stored.</returns>
    [JobDisplayName("Export Runtime Model by deep graph from tenant '{0}'")]
    [AutomaticRetry(Attempts = 0)]
    Task<string> ExportRtModelByDeepGraphAsync(ExportRtByDeepGraphCommandRequest rtByDeepGraphCommandRequest,
        IBotCancellationToken? cancellationToken);
}