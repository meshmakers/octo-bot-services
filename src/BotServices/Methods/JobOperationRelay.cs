using Hangfire.Common;
using Hangfire.Storage.Monitoring;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.Jobs.Jobs.Operations;
using Meshmakers.Octo.Services.Methods.Operations;

namespace Meshmakers.Octo.Backend.BotServices.Methods;

/// <summary>How a Hangfire job that an async method started ended (AB#8447).</summary>
internal enum JobOperationEndKind
{
    /// <summary>The job succeeded.</summary>
    Succeeded,

    /// <summary>The job failed (or deleted itself after a failure).</summary>
    Failed,

    /// <summary>The job refused to run a second time or for a closed operation (at most once).</summary>
    Interrupted,

    /// <summary>The job failed before it started because it waited too long for its <c>DisableConcurrentExecution</c> lock.</summary>
    LockTimeout,

    /// <summary>The job no longer exists in the job storage.</summary>
    Missing
}

/// <summary>The end of a followed job: its kind and the job details (<c>null</c> for <see cref="JobOperationEndKind.Missing" />).</summary>
internal sealed record JobOperationEnd(JobOperationEndKind Kind, JobDetailsDto? Details);

/// <summary>
///     Starts and follows the Hangfire job behind an asynchronous CK method of the bot service (AB#8445 dump, AB#8447
///     restore and archive data): records the starter and the operation id as job parameters, relays the progress the job
///     writes into the operation record and reports how the job ended. The handlers turn the end into their result or
///     declared error. The job may run on another replica (shared job storage; job parameters are the channel).
/// </summary>
internal sealed class JobOperationRelay(IJobStorageAccessor jobStorage, TimeProvider timeProvider, ILogger logger)
{
    /// <summary>Progress parameter naming the Hangfire job.</summary>
    public const string JobIdParameter = "jobId";

    /// <summary>
    ///     Records the starter (from the validated caller principal, never from <c>CkMethodRequest.Caller</c>) and the
    ///     operation id on the enqueued job.
    /// </summary>
    public void RecordStart(string jobId, string tenantId, string operationId, OperationJobParameterNames names,
        string? subjectId, string? clientId)
    {
        JobStarterRecorder.Record(jobStorage, subjectId, clientId, jobId, tenantId, logger);
        TrySetJobParameter(jobId, names.OperationId, operationId);
    }

    /// <summary>
    ///     Reports <paramref name="queuedStage" />, then polls the job every <paramref name="pollInterval" />, relays every
    ///     new progress report and returns when the job ended.
    /// </summary>
    public async Task<JobOperationEnd> FollowAsync(string jobId, string tenantId, OperationJobParameterNames names,
        string queuedStage, TimeSpan pollInterval, ICkMethodOperationProgress progress, CancellationToken ct)
    {
        var last = new DumpProgress(0, queuedStage);
        await progress.ReportAsync(ToOperationProgress(last, jobId), ct);

        while (true)
        {
            await Task.Delay(pollInterval, timeProvider, ct);

            var details = jobStorage.GetJobDetails(jobId);
            if (details == null)
            {
                logger.LogWarning("Job '{JobId}' of tenant '{TenantId}' no longer exists", jobId, tenantId);
                return new JobOperationEnd(JobOperationEndKind.Missing, null);
            }

            var reported = DumpProgress.FromJson(ReadParameter(details, names.Progress));
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
                    return new JobOperationEnd(JobOperationEndKind.Succeeded, details);
                case "Failed":
                case "Deleted":
                    if (ReadParameter(details, names.Outcome) is DumpJobParameters.InterruptedOutcome
                        or DumpJobParameters.OperationClosedOutcome)
                    {
                        return new JobOperationEnd(JobOperationEndKind.Interrupted, details);
                    }

                    return new JobOperationEnd(
                        WaitedTooLongForTheLock(details) ? JobOperationEndKind.LockTimeout : JobOperationEndKind.Failed,
                        details);
            }
        }
    }

    /// <summary>
    ///     <c>Succeeded</c>, <c>Failed</c> or <c>Deleted</c> once the job ended, else <c>null</c>. The jobs delete
    ///     themselves after a failure (<c>OnAttemptsExceeded = Delete</c>), so <c>Deleted</c> is a failure too.
    /// </summary>
    public static string? FinalState(JobDetailsDto details)
    {
        var state = details.History.FirstOrDefault()?.StateName;
        return state is "Succeeded" or "Failed" or "Deleted" ? state : null;
    }

    /// <summary>
    ///     <c>true</c> when the job failed because it could not get the <c>DisableConcurrentExecution</c> lock in time (the
    ///     failed state names <c>DistributedLockTimeoutException</c>); the job then never started.
    /// </summary>
    public static bool WaitedTooLongForTheLock(JobDetailsDto details) =>
        details.History.Any(h => h.StateName == "Failed" && h.Data != null &&
                                 h.Data.TryGetValue("ExceptionType", out var type) &&
                                 type?.Contains("DistributedLockTimeoutException", StringComparison.Ordinal) == true);

    /// <summary>The report of the job as operation progress; <c>null</c> when it is out of the allowed shape.</summary>
    public static CkMethodOperationProgress? TryToOperationProgress(DumpProgress progress, string jobId)
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

    /// <summary>
    ///     The report of the job as operation progress, with the job id as parameter. A negative percentage means "not
    ///     known" (a row pump) and is stored without a percentage.
    /// </summary>
    public static CkMethodOperationProgress ToOperationProgress(DumpProgress progress, string jobId)
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

        int? percent = progress.Percent < 0 ? null : Math.Clamp(progress.Percent, 0, 100);
        return CkMethodOperationProgress.Create(percent, progress.StageCode,
            parameters.Select(p => (p.Key, p.Value)).ToArray());
    }

    /// <summary>
    ///     Reads a job parameter written by the job (<c>PerformContext.SetJobParameter</c>, Hangfire's JSON convention) or
    ///     by this service (plain string).
    /// </summary>
    public static string? ReadParameter(JobDetailsDto details, string name)
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

    private static bool SameProgress(DumpProgress a, DumpProgress b) =>
        a.Percent == b.Percent && a.StageCode == b.StageCode &&
        (a.Parameters ?? new Dictionary<string, string>()).OrderBy(p => p.Key)
        .SequenceEqual((b.Parameters ?? new Dictionary<string, string>()).OrderBy(p => p.Key));

    private void TrySetJobParameter(string jobId, string name, string value)
    {
        try
        {
            jobStorage.SetJobParameter(jobId, name, value);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not record parameter '{Name}' of job '{JobId}'", name, jobId);
        }
    }
}
