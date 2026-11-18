using Hangfire.Server;
using Meshmakers.Octo.Backend.Jobs.Jobs.Operations;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

public partial class DumpRepositoryJob
{
    private async Task<string?> RegisterDumpAsync(string tenantId, string? jobResult, string? jobId,
        CancellationToken ct)
    {
        if (jobResult == null || dumpHashRecorder == null)
        {
            return null;
        }

        return await dumpHashRecorder.RecordAsync(tenantId, jobResult, jobId, ct);
    }

    private void WriteDumpHash(PerformContext? performContext, string? sha256)
    {
        if (performContext == null || sha256 == null)
        {
            return;
        }

        try
        {
            performContext.SetJobParameter(DumpJobParameters.Sha256, sha256);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Hash of the dump of job '{JobId}' not stored as job parameter ({ExceptionType})",
                performContext.BackgroundJob.Id, exception.GetType().Name);
        }
    }
}
