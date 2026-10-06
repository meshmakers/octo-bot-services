using Meshmakers.Octo.Backend.Jobs.Services;
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
    TimeProvider? timeProvider = null) : ISecretSweepCoordinator
{
    private static readonly TimeSpan BackupTimeout = TimeSpan.FromHours(1);
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
        SecretSweepTrigger trigger, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var report = NewReport(tenantId, mode, trigger);

        if (mode == SecretSweepMode.Decrypt)
        {
            return await FinishAsync(report, SecretSweepOutcome.Skipped,
                "Decrypt is an emergency operation and not available through the secret sweep job.");
        }

        var writes = mode != SecretSweepMode.Verify;
        if (writes && !protector.IsConfigured)
        {
            return await FinishAsync(report, SecretSweepOutcome.Skipped,
                "Secret encryption keys are not configured on the bot (SecretEncryption:Keys / ActiveKeyId); " +
                "only Verify is possible.");
        }

        try
        {
            if (writes)
            {
                var backupError = await TakeBackupAsync(report, cancellationToken);
                if (backupError != null)
                {
                    if (options.Value.RequirePreSweepBackup)
                    {
                        return await FinishAsync(report, SecretSweepOutcome.Skipped,
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
            await FinishAsync(report, SecretSweepOutcome.Failed, "The sweep was cancelled.");
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Secret sweep {Mode} of tenant '{TenantId}' failed", mode, tenantId);
            return await FinishAsync(report, SecretSweepOutcome.Failed, Describe(e));
        }

        return await FinishAsync(report, OutcomeOfSteps(report), report.Reason);
    }

    /// <inheritdoc />
    public async Task<SecretSweepReport> RunAfterRestoreAsync(string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var report = NewReport(tenantId, SecretSweepMode.Encrypt, SecretSweepTrigger.Restore);

        if (!options.Value.RunAfterRestore)
        {
            return await FinishAsync(report, SecretSweepOutcome.Skipped,
                "Disabled by configuration (Bot:SecretSweep:RunAfterRestore=false).");
        }

        if (!protector.IsConfigured)
        {
            return await FinishAsync(report, SecretSweepOutcome.Skipped,
                "Secret encryption keys are not configured on the bot; restored secrets were left as found. " +
                "Run the Encrypt sweep once keys are configured.");
        }

        try
        {
            // Decision 5: values of another key ring (cross-environment / child-tenant restore) cannot be
            // decrypted here - they become "not set" and are reported for re-entry. On a same-environment
            // restore every key id is known and this step changes nothing. No pre-sweep dump: the uploaded
            // backup is the state before this sweep.
            var clear = await RunStepAsync(report, SecretSweepMode.ClearUnknownKid, cancellationToken);
            report.SecretsToReEnter.AddRange(clear.Cleared);

            // Older (pre phase 4) dumps carry plaintext / enc:v1.
            await RunStepAsync(report, SecretSweepMode.Encrypt, cancellationToken);
            await RunStepAsync(report, SecretSweepMode.Verify, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(report, SecretSweepOutcome.Failed, "The sweep was cancelled.");
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Post-restore secret sweep of tenant '{TenantId}' failed", tenantId);
            return await FinishAsync(report, SecretSweepOutcome.Failed, Describe(e));
        }

        return await FinishAsync(report, OutcomeOfSteps(report), null);
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
        var sweepOptions = new SecretSweepOptions { BatchSize = Math.Max(1, options.Value.BatchSize) };
        var result = await maintenanceService.SweepTenantAsync(report.TenantId, mode, sweepOptions,
            cancellationToken);
        var step = SecretSweepReportMapper.ToStepReport(result);
        report.Steps.Add(step);
        return step;
    }

    /// <summary>
    ///     Takes the pre-sweep dump. Returns <c>null</c> on success, otherwise a value-free reason.
    /// </summary>
    private async Task<string?> TakeBackupAsync(SecretSweepReport report, CancellationToken cancellationToken)
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
            return $"{e.GetType().Name}: {e.Message}";
        }
    }

    private static SecretSweepOutcome OutcomeOfSteps(SecretSweepReport report)
    {
        return report.Steps.All(s => s.Success)
            ? SecretSweepOutcome.Succeeded
            : SecretSweepOutcome.CompletedWithFailures;
    }

    private async Task<SecretSweepReport> FinishAsync(SecretSweepReport report, SecretSweepOutcome outcome,
        string? reason)
    {
        report.Outcome = outcome;
        report.Reason = reason;
        report.CompletedAt = _time.GetUtcNow().UtcDateTime;

        var finalStep = report.Steps.LastOrDefault();
        if (finalStep != null)
        {
            report.RemainingLegacyValues = finalStep.Totals.Legacy;
            EvaluateStrictMode(report, finalStep);
        }

        Log(report);

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
            "{Rewritten} value(s) rewritten, {ReEnter} secret(s) to re-enter, backup '{BackupFileName}'",
            report.Mode, report.Trigger, report.TenantId, report.Outcome,
            report.Reason == null ? string.Empty : " - ", report.Reason ?? string.Empty,
            final?.Plaintext ?? 0, final?.EncV1 ?? 0, final?.EncV2 ?? 0, final?.UnknownKeyId ?? 0,
            report.Steps.Sum(s => s.Totals.Failed), report.Steps.Sum(s => s.ValuesRewritten),
            report.SecretsToReEnter.Count, report.BackupFileName ?? "<none>");

        foreach (var secret in report.SecretsToReEnter)
        {
            logger.LogWarning(
                "Secret to re-enter in tenant '{TenantId}': {CkTypeId} {RtId} {AttributePath} (key id '{KeyId}' unknown here)",
                report.TenantId, secret.CkTypeId, secret.RtId, secret.AttributePath, secret.KeyId);
        }
    }

    private static string Describe(Exception e)
    {
        // Engine exceptions of the secret area are value-free by contract (AB#5532).
        return $"{e.GetType().Name}: {e.Message}";
    }
}
