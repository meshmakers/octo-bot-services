using Hangfire;
using Hangfire.Common;
using Hangfire.Storage.Monitoring;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs.Operations;
using Meshmakers.Octo.ConstructionKit.Models.System.Bot.Generated.System.Bot.v3;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.BotServices.Methods;

/// <summary>
///     Handler of the asynchronous method <c>System.Bot/TenantRepository.Dump-1</c> (AB#8445, Feature AB#5694 F3.6): a
///     tenant administrator starts a repository dump and follows it by the operation handle.
/// </summary>
/// <remarks>
///     <para>
///         The handler accepts the call at once. The work of the operation then enqueues the <b>same Hangfire dump job</b>
///         the REST endpoint <c>{tenantId}/v1/jobs/dump-repository</c> enqueues (<see cref="IDumpRepositoryJob.RunForOperation" />,
///         <c>AutomaticRetry(Attempts = 0)</c>), records the starter and the operation id as job parameters, and waits for
///         the job: it relays the progress the job writes (job parameter <c>OctoDumpProgress</c>) into the operation record
///         and completes with <see cref="RtRepositoryDumpResultRecord" /> (job id and download path) or a declared error.
///         <c>system/v1/jobs/{jobId}</c> and the download endpoints answer for the job exactly as for a REST dump.
///     </para>
///     <para>
///         Enqueuing inside the work (not in <see cref="HandleAsync" />) keeps the at-most-once rule of OD-5: the job only
///         exists when the operation record was written. A stopped bot service ends the operation as
///         <c>METHOD_OUTCOME_UNKNOWN</c> / <c>handler-lost</c> (method runtime) and the job refuses a second start.
///     </para>
///     <para>
///         The caller's authorization (<c>TenantManagement</c>, tenant of the token) is enforced by the method host from
///         the CK definition before this handler runs; the dump runs for the tenant of the call only.
///     </para>
/// </remarks>
public sealed class TenantRepositoryDumpHandler(
    IBackgroundJobClient backgroundJobClient,
    IJobStorageAccessor jobStorage,
    IOptions<RepositoryDumpMethodOptions> options,
    TimeProvider timeProvider,
    ILogger<TenantRepositoryDumpHandler> logger)
    : ICkMethodHandler<TenantRepositoryDumpParameters, TenantRepositoryDumpResult>
{
    /// <summary>Declared error: the dump job failed.</summary>
    public const string DumpFailed = "DUMP_FAILED";

    /// <summary>Declared error: the dump job was interrupted and is not started again.</summary>
    public const string DumpInterrupted = "DUMP_INTERRUPTED";

    /// <summary>Error and progress parameter naming the Hangfire job.</summary>
    public const string JobIdParameter = "jobId";

    /// <inheritdoc />
    public Task<CkMethodOutcome<TenantRepositoryDumpResult>> HandleAsync(
        CkMethodContext<TenantRepositoryDumpParameters> context, CancellationToken cancellationToken)
    {
        // Copy what the work needs; the work runs detached and never keeps the caller's token.
        var tenantId = context.TenantId;
        var includeArchiveData = context.Parameters.IncludeArchiveData ?? false;
        var subjectId = context.CallerPrincipal?.SubjectId;
        var clientId = context.CallerPrincipal?.ClientId;

        return Task.FromResult(CkMethodOutcome<TenantRepositoryDumpResult>.Accepted((progress, ct) =>
            RunAsync(tenantId, includeArchiveData, subjectId, clientId, progress, ct)));
    }

    internal async Task<CkMethodOutcome<TenantRepositoryDumpResult>> RunAsync(string tenantId, bool includeArchiveData,
        string? subjectId, string? clientId, ICkMethodOperationProgress progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var operationId = progress.OperationId;
        var jobId = backgroundJobClient.Enqueue<IDumpRepositoryJob>(job =>
            job.RunForOperation(tenantId, includeArchiveData, operationId, null, BotCancellationToken.Null));

        JobStarterRecorder.Record(jobStorage, subjectId, clientId, jobId, tenantId, logger);
        TrySetJobParameter(jobId, DumpJobParameters.OperationId, operationId);
        logger.LogInformation(
            "Repository dump of tenant '{TenantId}' enqueued as job '{JobId}' for method operation '{OperationId}'",
            tenantId, jobId, operationId);

        var last = new DumpProgress(0, DumpStages.Queued);
        await progress.ReportAsync(ToOperationProgress(last, jobId), ct);

        while (true)
        {
            await Task.Delay(options.Value.PollInterval, timeProvider, ct);

            var details = jobStorage.GetJobDetails(jobId);
            if (details == null)
            {
                logger.LogWarning("Dump job '{JobId}' of tenant '{TenantId}' no longer exists", jobId, tenantId);
                return CkMethodOutcome<TenantRepositoryDumpResult>.Fail(DumpFailed, (JobIdParameter, jobId));
            }

            var reported = DumpProgress.FromJson(ReadParameter(details, DumpJobParameters.Progress));
            if (reported != null && !SameProgress(reported, last))
            {
                last = reported;
                if (TryToOperationProgress(reported, jobId) is { } operationProgress)
                {
                    await progress.ReportAsync(operationProgress, ct);
                }
            }

            switch (FinalState(details))
            {
                case "Succeeded":
                    return CkMethodOutcome<TenantRepositoryDumpResult>.Ok(new TenantRepositoryDumpResult
                    {
                        Value = new RtRepositoryDumpResultRecord
                        {
                            JobId = jobId,
                            DownloadPath = DownloadPath(tenantId, jobId)
                        }
                    });
                case "Failed":
                case "Deleted":
                    var interrupted = ReadParameter(details, DumpJobParameters.Outcome) is
                        DumpJobParameters.InterruptedOutcome or DumpJobParameters.OperationClosedOutcome;
                    logger.LogWarning("Dump job '{JobId}' of tenant '{TenantId}' ended without a dump ({Code})", jobId,
                        tenantId, interrupted ? DumpInterrupted : DumpFailed);
                    return CkMethodOutcome<TenantRepositoryDumpResult>.Fail(
                        interrupted ? DumpInterrupted : DumpFailed, (JobIdParameter, jobId));
            }
        }
    }

    /// <summary>The relative download path of the dump on this service (the AB#5070 tenant route).</summary>
    public static string DownloadPath(string tenantId, string jobId) =>
        $"{tenantId}/v1/jobs/download?id={Uri.EscapeDataString(jobId)}";

    /// <summary>
    ///     <c>Succeeded</c>, <c>Failed</c> or <c>Deleted</c> once the job ended, else <c>null</c>. The dump job deletes
    ///     itself after a failure (<c>OnAttemptsExceeded = Delete</c>), so <c>Deleted</c> is a failure too.
    /// </summary>
    internal static string? FinalState(JobDetailsDto details)
    {
        var state = details.History.FirstOrDefault()?.StateName;
        return state is "Succeeded" or "Failed" or "Deleted" ? state : null;
    }

    /// <summary>The report of the job as operation progress; <c>null</c> when it is out of the allowed shape.</summary>
    internal static CkMethodOperationProgress? TryToOperationProgress(DumpProgress progress, string jobId)
    {
        try
        {
            return ToOperationProgress(progress, jobId);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal static CkMethodOperationProgress ToOperationProgress(DumpProgress progress, string jobId)
    {
        var parameters = new Dictionary<string, string> { [JobIdParameter] = jobId };
        foreach (var (name, value) in progress.Parameters ?? new Dictionary<string, string>())
        {
            if (parameters.Count >= CkMethodOperationProgress.MaxParameters)
            {
                break;
            }

            parameters.TryAdd(name, value);
        }

        return CkMethodOperationProgress.Create(Math.Clamp(progress.Percent, 0, 100), progress.StageCode,
            parameters.Select(p => (p.Key, p.Value)).ToArray());
    }

    private static bool SameProgress(DumpProgress a, DumpProgress b) =>
        a.Percent == b.Percent && a.StageCode == b.StageCode &&
        (a.Parameters ?? new Dictionary<string, string>()).OrderBy(p => p.Key)
        .SequenceEqual((b.Parameters ?? new Dictionary<string, string>()).OrderBy(p => p.Key));

    /// <summary>
    ///     Reads a job parameter written by the job (<c>PerformContext.SetJobParameter</c>, Hangfire's JSON convention) or
    ///     by this service (plain string).
    /// </summary>
    internal static string? ReadParameter(JobDetailsDto details, string name)
    {
        if (details.Properties == null || !details.Properties.TryGetValue(name, out var raw) || raw == null)
        {
            return null;
        }

        try
        {
            return raw.StartsWith('"') ? SerializationHelper.Deserialize<string>(raw) : raw;
        }
        catch (Exception)
        {
            return raw;
        }
    }

    private void TrySetJobParameter(string jobId, string name, string value)
    {
        try
        {
            jobStorage.SetJobParameter(jobId, name, value);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not record parameter '{Name}' of dump job '{JobId}'", name, jobId);
        }
    }
}

/// <summary>Options of the dump method (section <c>Methods:RepositoryDump</c>, env <c>OCTO_Methods__RepositoryDump__*</c>).</summary>
public sealed class RepositoryDumpMethodOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Methods:RepositoryDump";

    /// <summary>How often the work reads the job state and progress. Default 2 s, at least 100 ms.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
}
