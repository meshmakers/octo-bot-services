using Hangfire;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.Backend.Jobs.Jobs.Operations;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Bot.Generated.System.Bot.v3;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.Options;
using ArchiveImportMode = Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ArchiveImportMode;

namespace Meshmakers.Octo.Backend.BotServices.Methods;

/// <summary>
///     Handler of the asynchronous method <c>System.Bot/TenantRepository.ExportArchiveData-1</c> (AB#8447, Feature AB#5694
///     F3.6): exports the rows of an archive of the tenant to a downloadable ZIP and completes with the job id, the row
///     count and the download path.
/// </summary>
/// <remarks>
///     The work enqueues the same Hangfire job as <c>{tenantId}/v1/jobs/export-archive-data</c>
///     (<see cref="IExportArchiveDataJob.RunForOperation" />, at most once) and relays its progress (rows so far, no
///     percentage: the total is known only at the end). The file is downloaded through the existing tenant route
///     <c>{tenantId}/v1/jobs/download?id={jobId}</c>.
/// </remarks>
public sealed class TenantRepositoryExportArchiveDataHandler(
    IBackgroundJobClient backgroundJobClient,
    IJobStorageAccessor jobStorage,
    IOptions<BotJobMethodOptions> options,
    TimeProvider timeProvider,
    ILogger<TenantRepositoryExportArchiveDataHandler> logger)
    : ICkMethodHandler<TenantRepositoryExportArchiveDataParameters, TenantRepositoryExportArchiveDataResult>
{
    /// <summary>Declared error: the export job failed.</summary>
    public const string ExportFailed = "ARCHIVE_EXPORT_FAILED";

    /// <summary>Declared error: the export job was interrupted and is not started again.</summary>
    public const string ExportInterrupted = "ARCHIVE_EXPORT_INTERRUPTED";

    /// <inheritdoc />
    public Task<CkMethodOutcome<TenantRepositoryExportArchiveDataResult>> HandleAsync(
        CkMethodContext<TenantRepositoryExportArchiveDataParameters> context, CancellationToken cancellationToken)
    {
        var parameters = context.Parameters;
        if (!ArchiveIds.IsValid(parameters.ArchiveRtId))
        {
            return Task.FromResult(
                CkMethodOutcome<TenantRepositoryExportArchiveDataResult>.ValidationFailed("archiveRtId", "invalid"));
        }

        var fromUtc = parameters.FromUtc?.ToUniversalTime();
        var toUtc = parameters.ToUtc?.ToUniversalTime();
        if (fromUtc != null && toUtc != null && fromUtc >= toUtc)
        {
            return Task.FromResult(
                CkMethodOutcome<TenantRepositoryExportArchiveDataResult>.ValidationFailed("toUtc", "not-after-fromUtc"));
        }

        var tenantId = context.TenantId;
        var archiveRtId = parameters.ArchiveRtId;
        var subjectId = context.CallerPrincipal?.SubjectId;
        var clientId = context.CallerPrincipal?.ClientId;
        return Task.FromResult(CkMethodOutcome<TenantRepositoryExportArchiveDataResult>.Accepted((progress, ct) =>
            RunAsync(tenantId, archiveRtId, fromUtc, toUtc, subjectId, clientId, progress, ct)));
    }

    internal async Task<CkMethodOutcome<TenantRepositoryExportArchiveDataResult>> RunAsync(string tenantId,
        string archiveRtId, DateTime? fromUtc, DateTime? toUtc, string? subjectId, string? clientId,
        ICkMethodOperationProgress progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var operationId = progress.OperationId;
        var jobId = backgroundJobClient.Enqueue<IExportArchiveDataJob>(job =>
            job.RunForOperation(tenantId, archiveRtId, fromUtc, toUtc, operationId, null, BotCancellationToken.Null));

        var relay = new JobOperationRelay(jobStorage, timeProvider, logger);
        relay.RecordStart(jobId, tenantId, operationId, OperationJobParameterNames.Generic, subjectId, clientId);
        logger.LogInformation(
            "Archive data export of tenant '{TenantId}' enqueued as job '{JobId}' for method operation '{OperationId}'",
            tenantId, jobId, operationId);

        var end = await relay.FollowAsync(jobId, tenantId, OperationJobParameterNames.Generic,
            ArchiveDataStages.Queued, options.Value.PollInterval, progress, ct);
        if (end.Kind == JobOperationEndKind.Succeeded)
        {
            var summary = ArchiveDataOperationSummary.FromJson(
                JobOperationRelay.ReadParameter(end.Details!, OperationJobParameters.Result));
            return CkMethodOutcome<TenantRepositoryExportArchiveDataResult>.Ok(
                new TenantRepositoryExportArchiveDataResult
                {
                    Value = new RtArchiveDataExportResultRecord
                    {
                        JobId = jobId,
                        DownloadPath = TenantRepositoryDumpHandler.DownloadPath(tenantId, jobId),
                        RowCount = summary?.RowCount ?? 0
                    }
                });
        }

        var code = end.Kind == JobOperationEndKind.Interrupted ? ExportInterrupted : ExportFailed;
        logger.LogWarning("Archive data export job '{JobId}' of tenant '{TenantId}' did not succeed ({Code})", jobId,
            tenantId, code);
        return CkMethodOutcome<TenantRepositoryExportArchiveDataResult>.Fail(code,
            BotJobMethodErrors.FailureParameters(end, jobId));
    }
}

/// <summary>
///     Handler of the asynchronous method <c>System.Bot/TenantRepository.ImportArchiveData-1</c> (AB#8447, Feature AB#5694
///     F3.6): imports the rows of an uploaded archive data export into a disabled archive of the tenant.
/// </summary>
/// <remarks>
///     <see cref="HandleAsync" /> checks the upload and answers <c>UPLOAD_NOT_FOUND</c> at once when it is not usable. The
///     work enqueues the same Hangfire job as <c>{tenantId}/v1/jobs/import-archive-data-from-upload</c>
///     (<see cref="IImportArchiveDataJob.RunForOperation" />, at most once); the schema check, the "archive must be
///     disabled" guard and the rollup freeze stay in the job, their failures end as <c>ARCHIVE_IMPORT_FAILED</c> with the
///     job id (the job's error names the reason).
/// </remarks>
public sealed class TenantRepositoryImportArchiveDataHandler(
    IBackgroundJobClient backgroundJobClient,
    IJobStorageAccessor jobStorage,
    ISharedUploadStore sharedUploads,
    IBackupFileStorageService backupFileStorage,
    IOptions<BotJobMethodOptions> options,
    TimeProvider timeProvider,
    ILogger<TenantRepositoryImportArchiveDataHandler> logger)
    : ICkMethodHandler<TenantRepositoryImportArchiveDataParameters, TenantRepositoryImportArchiveDataResult>
{
    /// <summary>Declared error: the import job failed.</summary>
    public const string ImportFailed = "ARCHIVE_IMPORT_FAILED";

    /// <summary>Declared error: the import job was interrupted and is not started again.</summary>
    public const string ImportInterrupted = "ARCHIVE_IMPORT_INTERRUPTED";

    /// <inheritdoc />
    public async Task<CkMethodOutcome<TenantRepositoryImportArchiveDataResult>> HandleAsync(
        CkMethodContext<TenantRepositoryImportArchiveDataParameters> context, CancellationToken cancellationToken)
    {
        var parameters = context.Parameters;
        if (string.IsNullOrWhiteSpace(parameters.TusFileId))
        {
            return CkMethodOutcome<TenantRepositoryImportArchiveDataResult>.ValidationFailed("tusFileId", "required");
        }

        if (!ArchiveIds.IsValid(parameters.ArchiveRtId))
        {
            return CkMethodOutcome<TenantRepositoryImportArchiveDataResult>.ValidationFailed("archiveRtId", "invalid");
        }

        var tenantId = context.TenantId;
        var tusFileId = parameters.TusFileId;
        var archiveRtId = parameters.ArchiveRtId;
        var mode = parameters.Mode == RtArchiveImportModeEnum.Upsert
            ? ArchiveImportMode.Upsert
            : ArchiveImportMode.InsertOnly;
        var subjectId = context.CallerPrincipal?.SubjectId;
        var clientId = context.CallerPrincipal?.ClientId;

        var problem = await StagedUploadCheck.CheckAsync(sharedUploads, backupFileStorage, tenantId, tusFileId,
            cancellationToken);
        if (problem != StagedUploadProblem.None)
        {
            return CkMethodOutcome<TenantRepositoryImportArchiveDataResult>.Fail(BotJobMethodErrors.UploadNotFound,
                BotJobMethodErrors.UploadReason(problem));
        }

        return CkMethodOutcome<TenantRepositoryImportArchiveDataResult>.Accepted((progress, ct) =>
            RunAsync(tenantId, archiveRtId, tusFileId, mode, subjectId, clientId, progress, ct));
    }

    internal async Task<CkMethodOutcome<TenantRepositoryImportArchiveDataResult>> RunAsync(string tenantId,
        string archiveRtId, string tusFileId, ArchiveImportMode mode, string? subjectId, string? clientId,
        ICkMethodOperationProgress progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var operationId = progress.OperationId;
        var jobId = backgroundJobClient.Enqueue<IImportArchiveDataJob>(job =>
            job.RunForOperation(tenantId, archiveRtId, tusFileId, mode, operationId, null, BotCancellationToken.Null));

        var relay = new JobOperationRelay(jobStorage, timeProvider, logger);
        relay.RecordStart(jobId, tenantId, operationId, OperationJobParameterNames.Generic, subjectId, clientId);
        logger.LogInformation(
            "Archive data import of tenant '{TenantId}' enqueued as job '{JobId}' for method operation '{OperationId}'",
            tenantId, jobId, operationId);

        var end = await relay.FollowAsync(jobId, tenantId, OperationJobParameterNames.Generic,
            ArchiveDataStages.Queued, options.Value.PollInterval, progress, ct);
        if (end.Kind == JobOperationEndKind.Succeeded)
        {
            var summary = ArchiveDataOperationSummary.FromJson(
                JobOperationRelay.ReadParameter(end.Details!, OperationJobParameters.Result));
            return CkMethodOutcome<TenantRepositoryImportArchiveDataResult>.Ok(
                new TenantRepositoryImportArchiveDataResult
                {
                    Value = new RtArchiveDataImportResultRecord
                    {
                        JobId = jobId,
                        RowCount = summary?.RowCount ?? 0
                    }
                });
        }

        var code = end.Kind == JobOperationEndKind.Interrupted ? ImportInterrupted : ImportFailed;
        logger.LogWarning("Archive data import job '{JobId}' of tenant '{TenantId}' did not succeed ({Code})", jobId,
            tenantId, code);
        return CkMethodOutcome<TenantRepositoryImportArchiveDataResult>.Fail(code,
            BotJobMethodErrors.FailureParameters(end, jobId));
    }
}

/// <summary>Validation of an archive runtime id parameter (AB#8447).</summary>
internal static class ArchiveIds
{
    /// <summary><c>true</c> when <paramref name="archiveRtId" /> is a well-formed runtime id (24 hex characters).</summary>
    public static bool IsValid(string? archiveRtId) =>
        !string.IsNullOrWhiteSpace(archiveRtId) && OctoObjectId.TryParse(archiveRtId, out _);
}
