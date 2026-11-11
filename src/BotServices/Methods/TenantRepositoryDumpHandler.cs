using Hangfire;
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
    public const string JobIdParameter = JobOperationRelay.JobIdParameter;

    /// <summary>Error parameter with the reason of a <see cref="DumpFailed" /> that the caller can act on.</summary>
    public const string ReasonParameter = "reason";

    /// <summary>
    ///     <see cref="ReasonParameter" />: the dump did not start because another method dump held the lock for longer than
    ///     the lock timeout (10 min); try again later.
    /// </summary>
    public const string AnotherDumpRunningReason = "another-dump-running";

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

        var relay = new JobOperationRelay(jobStorage, timeProvider, logger);
        relay.RecordStart(jobId, tenantId, operationId, OperationJobParameterNames.Dump, subjectId, clientId);
        logger.LogInformation(
            "Repository dump of tenant '{TenantId}' enqueued as job '{JobId}' for method operation '{OperationId}'",
            tenantId, jobId, operationId);

        var end = await relay.FollowAsync(jobId, tenantId, OperationJobParameterNames.Dump, DumpStages.Queued,
            options.Value.PollInterval, progress, ct);
        switch (end.Kind)
        {
            case JobOperationEndKind.Succeeded:
                return CkMethodOutcome<TenantRepositoryDumpResult>.Ok(new TenantRepositoryDumpResult
                {
                    Value = new RtRepositoryDumpResultRecord
                    {
                        JobId = jobId,
                        DownloadPath = DownloadPath(tenantId, jobId)
                    }
                });
            case JobOperationEndKind.Interrupted:
                logger.LogWarning("Dump job '{JobId}' of tenant '{TenantId}' ended without a dump ({Code})", jobId,
                    tenantId, DumpInterrupted);
                return CkMethodOutcome<TenantRepositoryDumpResult>.Fail(DumpInterrupted, (JobIdParameter, jobId));
            case JobOperationEndKind.LockTimeout:
                // Method dumps are serialized (DisableConcurrentExecution, 10 min to get the lock): a dump that waited
                // longer for another one fails before it starts. The caller can simply try again.
                logger.LogWarning("Dump job '{JobId}' of tenant '{TenantId}' ended without a dump ({Code})", jobId,
                    tenantId, DumpFailed);
                return CkMethodOutcome<TenantRepositoryDumpResult>.Fail(DumpFailed, (JobIdParameter, jobId),
                    (ReasonParameter, AnotherDumpRunningReason));
            default:
                logger.LogWarning("Dump job '{JobId}' of tenant '{TenantId}' ended without a dump ({Code})", jobId,
                    tenantId, DumpFailed);
                return CkMethodOutcome<TenantRepositoryDumpResult>.Fail(DumpFailed, (JobIdParameter, jobId));
        }
    }

    /// <summary>The relative download path of the dump on this service (the AB#5070 tenant route).</summary>
    public static string DownloadPath(string tenantId, string jobId) =>
        $"{tenantId}/v1/jobs/download?id={Uri.EscapeDataString(jobId)}";

    /// <summary>See <see cref="JobOperationRelay.FinalState" />.</summary>
    internal static string? FinalState(JobDetailsDto details) => JobOperationRelay.FinalState(details);

    /// <summary>See <see cref="JobOperationRelay.WaitedTooLongForTheLock" />.</summary>
    internal static bool WaitedTooLongForTheLock(JobDetailsDto details) =>
        JobOperationRelay.WaitedTooLongForTheLock(details);

    /// <summary>See <see cref="JobOperationRelay.TryToOperationProgress" />.</summary>
    internal static CkMethodOperationProgress? TryToOperationProgress(DumpProgress progress, string jobId) =>
        JobOperationRelay.TryToOperationProgress(progress, jobId);

    /// <summary>See <see cref="JobOperationRelay.ToOperationProgress" />.</summary>
    internal static CkMethodOperationProgress ToOperationProgress(DumpProgress progress, string jobId) =>
        JobOperationRelay.ToOperationProgress(progress, jobId);

    /// <summary>See <see cref="JobOperationRelay.ReadParameter" />.</summary>
    internal static string? ReadParameter(JobDetailsDto details, string name) =>
        JobOperationRelay.ReadParameter(details, name);
}

/// <summary>Options of the dump method (section <c>Methods:RepositoryDump</c>, env <c>OCTO_Methods__RepositoryDump__*</c>).</summary>
public sealed class RepositoryDumpMethodOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Methods:RepositoryDump";

    /// <summary>How often the work reads the job state and progress. Default 2 s, at least 100 ms.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
}
