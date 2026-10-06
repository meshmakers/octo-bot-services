using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.Jobs.Secrets;

/// <inheritdoc />
public class SecretSweepCoordinator(
    ILogger<SecretSweepCoordinator> logger,
    ISystemContext systemContext,
    ISecretMaintenanceService maintenanceService,
    ISecretAttributeProtector protector,
    IBackupFileStorageService backupFileStorage,
    ISecretSweepReportStore reportStore,
    IOptions<SecretSweepJobOptions> options,
    TimeProvider? timeProvider = null,
    ISecretSweepTenantLock? tenantLock = null,
    ISecretSweepRunStore? runStore = null) : ISecretSweepCoordinator
{
    private static readonly TimeSpan BackupTimeout = TimeSpan.FromHours(1);

    // A sweep that finds its tenant busy is skipped (visible in the report and the job state); the
    // post-restore sweep waits for a running sweep (e.g. the recurring Verify) instead.
    internal static readonly TimeSpan SweepLockTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan RestoreLockTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    ///     Reason of a post-restore run on a bot without key ring (AB#5539): the outcome is
    ///     <see cref="SecretSweepOutcome.Succeeded" /> (or <see cref="SecretSweepOutcome.CompletedWithFailures" />
    ///     when the Verify itself reported failures), the re-entry list is complete, nothing was written.
    /// </summary>
    internal const string NoKeyRingRestoreReason =
        "No key ring configured: secrets were classified only; set the key ring and run Encrypt";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetTenantIdsAsync()
    {
        if (!await systemContext.IsSystemTenantExistingAsync())
        {
            return [];
        }

        var tenantIds = new List<string> { systemContext.TenantId };
        using (var adminSession = await systemContext.GetAdminSessionAsync())
        {
            var resultSet = await systemContext.GetAllTenantsAsync(adminSession);
            tenantIds.AddRange(resultSet.Items.Select(t => t.TenantId));
        }

        return tenantIds.Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<SecretSweepReport> SweepTenantAsync(string tenantId, SecretSweepMode mode,
        SecretSweepTrigger trigger, SecretSweepRunInfo? runInfo, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var report = NewReport(tenantId, mode, trigger);
        var run = await StartRunAsync(report, runInfo);

        if (mode == SecretSweepMode.Decrypt)
        {
            return await FinishAsync(report, run, SecretSweepOutcome.Skipped,
                "Decrypt is an emergency operation and not available through the secret sweep job.");
        }

        var writes = mode != SecretSweepMode.Verify;
        if (writes && !protector.IsConfigured)
        {
            return await FinishAsync(report, run, SecretSweepOutcome.Skipped,
                "Secret encryption keys are not configured on the bot (SecretEncryption:Keys / ActiveKeyId); " +
                "only Verify is possible.");
        }

        using var held = tenantLock?.TryAcquire(tenantId, SweepLockTimeout);
        if (tenantLock != null && held == null)
        {
            return await FinishAsync(report, run, SecretSweepOutcome.Skipped,
                "Another secret sweep of this tenant is running; this run was not started.");
        }

        try
        {
            if (writes)
            {
                var backupError = await TakeBackupAsync(report, run, cancellationToken);
                if (backupError != null)
                {
                    if (options.Value.RequirePreSweepBackup)
                    {
                        return await FinishAsync(report, run, SecretSweepOutcome.Skipped,
                            $"The pre-sweep backup could not be taken ({backupError}); the sweep was not started. " +
                            "Fix the dump infrastructure or set Bot:SecretSweep:RequirePreSweepBackup=false deliberately.");
                    }

                    report.Reason = $"Pre-sweep backup not taken ({backupError}); not required by configuration.";
                    logger.LogWarning(
                        "Secret sweep {Mode} of tenant '{TenantId}' runs WITHOUT a pre-sweep backup: {Reason}",
                        mode, tenantId, backupError);
                }
            }

            await RunStepAsync(report, mode, cancellationToken);
            if (writes)
            {
                // The counts of a step are the forms as FOUND; a verify describes the state after the sweep
                // and refreshes the octo.secrets.values gauge with it.
                await RunStepAsync(report, SecretSweepMode.Verify, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(report, run, SecretSweepOutcome.Failed, "The sweep was cancelled.");
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Secret sweep {Mode} of tenant '{TenantId}' failed", mode, tenantId);
            return await FinishAsync(report, run, SecretSweepOutcome.Failed, Describe(e));
        }

        return await FinishAsync(report, run, OutcomeOfSteps(report), report.Reason);
    }

    /// <inheritdoc />
    public async Task<SecretSweepReport> RunAfterRestoreAsync(string tenantId, SecretSweepRunInfo? runInfo,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        // AB#5539: without a key ring the restore still produces the re-entry list - a key-free Verify
        // classifies the restored values by key id (every enc:v2 of a key id not in the empty ring and every
        // enc:v1 without legacy key is "key missing") and nothing is written. The run is recorded as Verify.
        var keyRingConfigured = protector.IsConfigured;
        var report = NewReport(tenantId, keyRingConfigured ? SecretSweepMode.Encrypt : SecretSweepMode.Verify,
            SecretSweepTrigger.Restore);
        var run = await StartRunAsync(report, runInfo);

        if (!options.Value.RunAfterRestore)
        {
            return await FinishAsync(report, run, SecretSweepOutcome.Skipped,
                "Disabled by configuration (Bot:SecretSweep:RunAfterRestore=false).");
        }

        using var held = tenantLock?.TryAcquire(tenantId, RestoreLockTimeout);
        if (tenantLock != null && held == null)
        {
            return await FinishAsync(report, run, SecretSweepOutcome.Skipped,
                "Another secret sweep of this tenant kept running; run the Encrypt sweep on the restored " +
                "tenant once it is done.");
        }

        if (!keyRingConfigured)
        {
            try
            {
                // Classification only: the engine's Verify needs no key material and never writes.
                await RunStepAsync(report, SecretSweepMode.Verify, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await FinishAsync(report, run, SecretSweepOutcome.Failed, "The sweep was cancelled.");
                throw;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Post-restore secret classification of tenant '{TenantId}' failed", tenantId);
                return await FinishAsync(report, run, SecretSweepOutcome.Failed, Describe(e));
            }

            return await FinishAsync(report, run, OutcomeOfSteps(report), NoKeyRingRestoreReason);
        }

        try
        {
            // Decisions 2026-10-06, item 2: nothing is deleted after a restore. Values of another key ring
            // (cross-environment / child-tenant restore) stay encrypted, read as "key missing" and are
            // reported for re-entry (Unreadable / SecretsToReEnter); they become readable again as soon as
            // their key is added to the ring. Only the admin sweep CleanupUnreadable removes them.
            await RunStepAsync(report, SecretSweepMode.Verify, cancellationToken);
            // Older (pre phase 4) dumps carry plaintext / enc:v1.
            await RunStepAsync(report, SecretSweepMode.Encrypt, cancellationToken);
            await RunStepAsync(report, SecretSweepMode.Verify, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(report, run, SecretSweepOutcome.Failed, "The sweep was cancelled.");
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Post-restore secret sweep of tenant '{TenantId}' failed", tenantId);
            return await FinishAsync(report, run, SecretSweepOutcome.Failed, Describe(e));
        }

        return await FinishAsync(report, run, OutcomeOfSteps(report), report.Reason);
    }

    private SecretSweepReport NewReport(string tenantId, SecretSweepMode mode, SecretSweepTrigger trigger)
    {
        var now = _time.GetUtcNow();
        var strictSince = options.Value.StrictModeSince;
        return new SecretSweepReport
        {
            TenantId = tenantId,
            Mode = mode,
            Trigger = trigger,
            StartedAt = now.UtcDateTime,
            ActiveKeyId = protector.IsConfigured ? protector.ActiveKeyId : null,
            StrictModeActive = strictSince != null && now >= strictSince.Value
        };
    }

    private async Task<SecretSweepStepReport> RunStepAsync(SecretSweepReport report, SecretSweepMode mode,
        CancellationToken cancellationToken)
    {
        // CleanupUnreadable is confirmed by the caller (API: confirm=true and the SecretManagement role) before
        // the job is enqueued; the engine requires the explicit flag on top.
        var sweepOptions = new SecretSweepOptions
        {
            BatchSize = Math.Max(1, options.Value.BatchSize),
            ConfirmCleanupUnreadable = mode == SecretSweepMode.CleanupUnreadable
        };
        var result = await maintenanceService.SweepTenantAsync(report.TenantId, mode, sweepOptions,
            cancellationToken);
        var step = SecretSweepReportMapper.ToStepReport(result);
        report.Steps.Add(step);
        return step;
    }

    /// <summary>
    ///     Takes the pre-sweep dump. Returns <c>null</c> on success, otherwise a value-free reason.
    /// </summary>
    private async Task<string?> TakeBackupAsync(SecretSweepReport report, SecretSweepRunDto run,
        CancellationToken cancellationToken)
    {
        string filePath;
        try
        {
            filePath = backupFileStorage.CreateSecretBackupFilePath(report.TenantId);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Cannot prepare the pre-sweep secret backup of tenant '{TenantId}'", report.TenantId);
            return $"backup directory not usable: {e.GetType().Name}";
        }

        try
        {
            logger.LogInformation("Taking pre-sweep secret backup of tenant '{TenantId}' to '{FilePath}'",
                report.TenantId, filePath);
            var result = await systemContext.BackupTenantAsync(report.TenantId, filePath, timeout: BackupTimeout,
                cancellationToken: cancellationToken);

            if (!result.Success || !File.Exists(filePath))
            {
                await backupFileStorage.DeleteFileAsync(filePath);
                // Exit code only: the tool output is not needed here and is logged by the backup service.
                return result.Success
                    ? "mongodump reported success but wrote no file"
                    : $"mongodump failed with exit code {result.ExitCode}";
            }

            backupFileStorage.RestrictToOwner(filePath);
            report.BackupFileName = Path.GetFileName(filePath);
            var createdAt = _time.GetUtcNow().UtcDateTime;
            run.Dump = new SecretSweepDumpDto
            {
                FileName = report.BackupFileName,
                Exists = true,
                SizeBytes = TryGetSize(filePath),
                CreatedAt = createdAt,
                ExpiresAt = createdAt.AddDays(Math.Max(0, options.Value.BackupRetentionDays))
            };
            await SaveRunAsync(report.TenantId, run);
            return null;
        }
        catch (OperationCanceledException)
        {
            await backupFileStorage.DeleteFileAsync(filePath);
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Pre-sweep secret backup of tenant '{TenantId}' failed", report.TenantId);
            await backupFileStorage.DeleteFileAsync(filePath);
            return Describe(e);
        }
    }

    private static SecretSweepOutcome OutcomeOfSteps(SecretSweepReport report)
    {
        return report.Steps.All(s => s.Success)
            ? SecretSweepOutcome.Succeeded
            : SecretSweepOutcome.CompletedWithFailures;
    }

    private async Task<SecretSweepRunDto> StartRunAsync(SecretSweepReport report, SecretSweepRunInfo? runInfo)
    {
        var run = new SecretSweepRunDto
        {
            RunId = string.IsNullOrWhiteSpace(runInfo?.RunId) ? Guid.NewGuid().ToString("N") : runInfo.RunId,
            Mode = (SecretSweepModeDto)(int)report.Mode,
            Trigger = (SecretSweepTriggerDto)(int)report.Trigger,
            Outcome = SecretSweepOutcomeDto.Running,
            StartedAt = report.StartedAt,
            TriggeredBy = string.IsNullOrWhiteSpace(runInfo?.TriggeredBy) ? null : runInfo.TriggeredBy
        };
        await SaveRunAsync(report.TenantId, run);
        return run;
    }

    private async Task SaveRunAsync(string tenantId, SecretSweepRunDto run)
    {
        if (runStore == null)
        {
            return;
        }

        try
        {
            // A dump deleted early while the sweep was still running stays deleted: the store merges the
            // deletion under the same per-tenant lock as the replace (no read-then-write window).
            await runStore.UpsertAsync(tenantId, run);
        }
        catch (Exception e)
        {
            // The run history is bookkeeping; losing an entry must not fail or skip the sweep.
            logger.LogWarning(e, "Could not store secret sweep run '{RunId}' of tenant '{TenantId}'", run.RunId,
                tenantId);
        }
    }

    private static long? TryGetSize(string filePath)
    {
        try
        {
            return new FileInfo(filePath).Length;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<SecretSweepReport> FinishAsync(SecretSweepReport report, SecretSweepRunDto run,
        SecretSweepOutcome outcome, string? reason)
    {
        report.Outcome = outcome;
        report.Reason = reason;
        report.CompletedAt = _time.GetUtcNow().UtcDateTime;
        report.PlaceholdersNormalized = report.Steps.Sum(s => s.PlaceholdersNormalized);
        report.SkippedLegacyV1KeyMissing = report.Steps.Sum(s => s.SkippedLegacyV1KeyMissing);

        var finalStep = report.Steps.LastOrDefault();
        if (finalStep != null)
        {
            report.RemainingLegacyValues = finalStep.Totals.Legacy;
            // The state after the sweep: what is still stored with a key id unknown here (kept, re-entry).
            report.Unreadable = finalStep.Unreadable.ToList();
            EvaluateStrictMode(report, finalStep);
        }

        report.SecretsToReEnter = report.Unreadable.Select(SecretValueReference.From)
            .Concat(report.Steps.SelectMany(s => s.Cleared))
            .ToList();

        Log(report);

        run.Outcome = (SecretSweepOutcomeDto)(int)outcome;
        run.CompletedAt = report.CompletedAt;
        run.Totals = ToDto(finalStep?.Totals ?? new SecretFormCountsReport());
        run.Reason = reason;
        run.PlaceholdersNormalized = report.PlaceholdersNormalized;
        run.SkippedLegacyV1KeyMissing = report.SkippedLegacyV1KeyMissing;
        run.UnreadableCount = report.Unreadable.Count;
        await SaveRunAsync(report.TenantId, run);

        try
        {
            await reportStore.SaveAsync(report);
        }
        catch (Exception e)
        {
            // The sweep itself is done; a lost report must not turn it into a failure.
            logger.LogWarning(e, "Could not store the secret sweep report of tenant '{TenantId}'", report.TenantId);
        }

        return report;
    }

    private void EvaluateStrictMode(SecretSweepReport report, SecretSweepStepReport finalStep)
    {
        if (!report.StrictModeActive)
        {
            SecretSweepStrictModeDiagnostics.Clear(report.TenantId);
            return;
        }

        var legacy = finalStep.Totals.Legacy;
        SecretSweepStrictModeDiagnostics.Record(report.TenantId, legacy);
        if (legacy == 0)
        {
            return;
        }

        report.StrictModeViolation = true;
        logger.LogError(
            "STRICT MODE VIOLATION: tenant '{TenantId}' still stores {Plaintext} clear-text and {EncV1} enc:v1 " +
            "Secret value(s) although strict mode is in force since {StrictModeSince:o}. Run the Encrypt sweep " +
            "and find the source (restore of an old dump, write path bypassing the engine)",
            report.TenantId, finalStep.Totals.Plaintext, finalStep.Totals.EncV1, options.Value.StrictModeSince);
    }

    private void Log(SecretSweepReport report)
    {
        var final = report.Steps.LastOrDefault()?.Totals;
        var level = report.Outcome switch
        {
            SecretSweepOutcome.Failed => LogLevel.Error,
            SecretSweepOutcome.Skipped or SecretSweepOutcome.CompletedWithFailures => LogLevel.Warning,
            _ => LogLevel.Information
        };

        logger.Log(level,
            "Secret sweep {Mode} ({Trigger}) of tenant '{TenantId}': {Outcome}{ReasonSeparator}{Reason}. Final state: " +
            "{Plaintext} plaintext, {EncV1} enc_v1, {EncV2} enc_v2, {UnknownKid} unknown kid, {Failed} failed; " +
            "{Rewritten} value(s) rewritten, {Placeholders} legacy placeholder(s) normalised, {SkippedLegacyV1} enc_v1 " +
            "kept (legacy key missing), {ReEnter} secret(s) to re-enter, backup '{BackupFileName}'",
            report.Mode, report.Trigger, report.TenantId, report.Outcome,
            report.Reason == null ? string.Empty : " - ", report.Reason ?? string.Empty,
            final?.Plaintext ?? 0, final?.EncV1 ?? 0, final?.EncV2 ?? 0, final?.UnknownKeyId ?? 0,
            report.Steps.Sum(s => s.Totals.Failed), report.Steps.Sum(s => s.ValuesRewritten),
            report.PlaceholdersNormalized, report.SkippedLegacyV1KeyMissing, report.SecretsToReEnter.Count, report.BackupFileName ?? "<none>");

        foreach (var secret in report.SecretsToReEnter)
        {
            logger.LogWarning(
                "Secret to re-enter in tenant '{TenantId}': {CkTypeId} {RtId} {AttributePath} (key id '{KeyId}' unknown here)",
                report.TenantId, secret.CkTypeId, secret.RtId, secret.AttributePath, secret.KeyId);
        }
    }

    private static SecretFormCountsReportDto ToDto(SecretFormCountsReport counts)
    {
        return new SecretFormCountsReportDto
        {
            NotSet = counts.NotSet,
            Placeholder = counts.Placeholder,
            Plaintext = counts.Plaintext,
            EncV1 = counts.EncV1,
            EncV2 = counts.EncV2,
            EncV2ByKeyId = new Dictionary<string, long>(counts.EncV2ByKeyId, StringComparer.OrdinalIgnoreCase),
            UnknownKeyId = counts.UnknownKeyId,
            UnknownKeyIdByKeyId =
                new Dictionary<string, long>(counts.UnknownKeyIdByKeyId, StringComparer.OrdinalIgnoreCase),
            Failed = counts.Failed,
            Total = counts.Total,
            Legacy = counts.Legacy
        };
    }

    internal static string Describe(Exception e)
    {
        // Engine exceptions of the secret area are value-free by contract (AB#5532). Any other message (database
        // driver, backup tool, serializer) may quote documents or connection strings: the report, the run list
        // and the Hangfire failure text only get its type name; the full exception is in the error log.
        var type = e.GetType();
        return type.Namespace?.StartsWith(SecretExceptionNamespace, StringComparison.Ordinal) == true
            ? $"{type.Name}: {e.Message}"
            : type.Name;
    }

    private const string SecretExceptionNamespace = "Meshmakers.Octo.Runtime.Contracts.Secrets";
}
