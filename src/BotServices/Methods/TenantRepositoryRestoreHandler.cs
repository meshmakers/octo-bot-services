using Hangfire;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs.Operations;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Bot.Generated.System.Bot.v3;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.BotServices.Methods;

/// <summary>
///     Handler of the asynchronous method <c>System.Bot/TenantRepository.Restore-1</c> (AB#8447, Feature AB#5694 F3.6): a
///     tenant administrator restores the tenant from an uploaded backup and follows it by the operation handle.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="HandleAsync" /> checks the upload (complete, not empty, of this tenant) and answers
///         <c>UPLOAD_NOT_FOUND</c> at once when it is not usable; otherwise it accepts. The work enqueues the <b>same
///         Hangfire restore job</b> the REST endpoint <c>{tenantId}/v1/jobs/restore-from-upload</c> enqueues
///         (<see cref="IRestoreRepositoryJob.RunForOperation" />, <c>AutomaticRetry(Attempts = 0)</c>, at most once), relays
///         its progress and completes with <see cref="RtRepositoryRestoreResultRecord" /> or a declared error.
///     </para>
///     <para>
///         The restore replaces the tenant <b>database</b>. The operation record lives in the system database
///         (<c>MethodOperations</c>, AB#8443), so it is neither replaced nor lost: the caller reads the outcome through
///         the handle after the restore. Nothing purges the tenant's operation records on a restore; only a deleted
///         tenant loses them (<c>TenantManagementConsumer</c>).
///     </para>
/// </remarks>
public sealed class TenantRepositoryRestoreHandler(
    IBackgroundJobClient backgroundJobClient,
    IJobStorageAccessor jobStorage,
    ISharedUploadStore sharedUploads,
    IBackupFileStorageService backupFileStorage,
    IOptions<BotJobMethodOptions> options,
    TimeProvider timeProvider,
    ILogger<TenantRepositoryRestoreHandler> logger)
    : ICkMethodHandler<TenantRepositoryRestoreParameters, TenantRepositoryRestoreResult>
{
    /// <summary>Declared error: the restore job failed.</summary>
    public const string RestoreFailed = "RESTORE_FAILED";

    /// <summary>Declared error: the restore job was interrupted and is not started again.</summary>
    public const string RestoreInterrupted = "RESTORE_INTERRUPTED";

    /// <inheritdoc />
    public async Task<CkMethodOutcome<TenantRepositoryRestoreResult>> HandleAsync(
        CkMethodContext<TenantRepositoryRestoreParameters> context, CancellationToken cancellationToken)
    {
        var parameters = context.Parameters;
        if (string.IsNullOrWhiteSpace(parameters.TusFileId))
        {
            return CkMethodOutcome<TenantRepositoryRestoreResult>.ValidationFailed("tusFileId", "required");
        }

        if (string.IsNullOrWhiteSpace(parameters.DatabaseName))
        {
            return CkMethodOutcome<TenantRepositoryRestoreResult>.ValidationFailed("databaseName", "required");
        }

        // Copy what the work needs; the work runs detached and never keeps the caller's token.
        var tenantId = context.TenantId;
        var tusFileId = parameters.TusFileId;
        var databaseName = parameters.DatabaseName;
        var oldDatabaseName = string.IsNullOrWhiteSpace(parameters.OldDatabaseName) ? null : parameters.OldDatabaseName;
        var restoreArchiveData = parameters.RestoreArchiveData ?? false;
        var subjectId = context.CallerPrincipal?.SubjectId;
        var clientId = context.CallerPrincipal?.ClientId;

        var problem = await StagedUploadCheck.CheckAsync(sharedUploads, backupFileStorage, tenantId, tusFileId,
            cancellationToken);
        if (problem != StagedUploadProblem.None)
        {
            return CkMethodOutcome<TenantRepositoryRestoreResult>.Fail(BotJobMethodErrors.UploadNotFound,
                BotJobMethodErrors.UploadReason(problem));
        }

        return CkMethodOutcome<TenantRepositoryRestoreResult>.Accepted((progress, ct) =>
            RunAsync(tenantId, databaseName, tusFileId, oldDatabaseName, restoreArchiveData, subjectId, clientId,
                progress, ct));
    }

    internal async Task<CkMethodOutcome<TenantRepositoryRestoreResult>> RunAsync(string tenantId, string databaseName,
        string tusFileId, string? oldDatabaseName, bool restoreArchiveData, string? subjectId, string? clientId,
        ICkMethodOperationProgress progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var operationId = progress.OperationId;
        var jobId = backgroundJobClient.Enqueue<IRestoreRepositoryJob>(job =>
            job.RunForOperation(tenantId, databaseName, tusFileId, oldDatabaseName, restoreArchiveData, operationId,
                null, BotCancellationToken.Null));

        var relay = new JobOperationRelay(jobStorage, timeProvider, logger);
        relay.RecordStart(jobId, tenantId, operationId, OperationJobParameterNames.Generic, subjectId, clientId);
        logger.LogInformation(
            "Repository restore of tenant '{TenantId}' enqueued as job '{JobId}' for method operation '{OperationId}'",
            tenantId, jobId, operationId);

        var end = await relay.FollowAsync(jobId, tenantId, OperationJobParameterNames.Generic, RestoreStages.Queued,
            options.Value.PollInterval, progress, ct);
        if (end.Kind == JobOperationEndKind.Succeeded)
        {
            var summary = RestoreOperationSummary.FromJson(
                JobOperationRelay.ReadParameter(end.Details!, OperationJobParameters.Result));
            return CkMethodOutcome<TenantRepositoryRestoreResult>.Ok(new TenantRepositoryRestoreResult
            {
                Value = new RtRepositoryRestoreResultRecord
                {
                    JobId = jobId,
                    ArchiveCount = summary?.ArchiveCount ?? 0,
                    FailedArchiveCount = summary?.FailedArchiveCount ?? 0,
                    SecretsToReEnterCount = summary?.SecretsToReEnterCount ?? 0
                }
            });
        }

        var code = end.Kind == JobOperationEndKind.Interrupted ? RestoreInterrupted : RestoreFailed;
        logger.LogWarning("Restore job '{JobId}' of tenant '{TenantId}' did not succeed ({Code})", jobId, tenantId,
            code);
        return CkMethodOutcome<TenantRepositoryRestoreResult>.Fail(code,
            BotJobMethodErrors.FailureParameters(end, jobId));
    }
}
