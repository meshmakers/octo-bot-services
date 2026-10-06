using Hangfire.Server;
using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Services.ArtifactStorage;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <inheritdoc />
public class RestorePreSweepDumpJob(
    ILogger<RestorePreSweepDumpJob> logger,
    ISystemContext systemContext,
    ISecretSweepRunStore runStore,
    IBotArtifactStorage artifactStorage,
    IBackupFileStorageService backupFileStorage,
    ISecretSweepCoordinator secretSweepCoordinator,
    ISecretSweepTenantLock? tenantLock = null) : IRestorePreSweepDumpJob
{
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromHours(1);

    /// <inheritdoc />
    public async Task<PreSweepDumpRestoreResult?> Run(string tenantId, string runId, string? triggeredBy,
        PerformContext? performContext, IBotCancellationToken? cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var ct = cancellationToken?.ShutdownToken ?? CancellationToken.None;

        if (!await systemContext.IsSystemTenantExistingAsync())
        {
            return null;
        }

        // The run is looked up in THIS tenant's history: a dump of another tenant is unreachable by construction.
        var run = (await runStore.GetRunsAsync(tenantId)).FirstOrDefault(r => r.RunId == runId);
        var dump = run?.Dump ?? throw new JobFailedException(
            $"Run '{runId}' of tenant '{tenantId}' has no pre-sweep dump.");
        if (dump.DeletedAt != null)
        {
            throw new JobFailedException(
                $"The pre-sweep dump of run '{runId}' of tenant '{tenantId}' was deleted at {dump.DeletedAt:o}.");
        }

        var tenantContext = await systemContext.FindTenantContextAsync(tenantId)
                            ?? throw new JobFailedException($"Tenant '{tenantId}' not found.");
        var databaseName = tenantContext.DatabaseName;

        string? scratchPath = null;
        try
        {
            string dumpPath;
            if (PreSweepDumps.IsStoredDump(dump.FileName))
            {
                scratchPath = artifactStorage.CreateScratchFilePath(PreSweepDumps.ScratchSuffix);
                try
                {
                    // Decrypted into a scratch file, never streamed into mongorestore: UnprotectAsync writes each
                    // verified chunk before it reads the next, so a tampered or truncated dump must be detected
                    // before anything is restored. A failure deletes the partial plaintext.
                    if (!await artifactStorage.TryWritePlainToFileAsync(ArtifactCategories.Presweep, tenantId,
                            dump.FileName, scratchPath, ct))
                    {
                        throw new JobFailedException(
                            $"The pre-sweep dump '{dump.FileName}' is no longer in the artifact store (expired?).");
                    }
                }
                catch (UnknownSecretKeyIdException e)
                {
                    throw new JobFailedException(
                        $"The pre-sweep dump '{dump.FileName}' is encrypted with key id '{e.KeyId}', which is not in " +
                        "the key ring (DumpKeyMissing). Add the key to SecretEncryption:Keys to restore it.", e);
                }

                dumpPath = scratchPath;
            }
            else if (PreSweepDumps.IsLegacyDump(dump.FileName))
            {
                // A dump written before AB#5561: plaintext on the local disk, restored from where it is.
                dumpPath = backupFileStorage.GetSecretBackupFilePath(tenantId, dump.FileName);
                if (!File.Exists(dumpPath))
                {
                    throw new JobFailedException($"The legacy pre-sweep dump '{dump.FileName}' no longer exists.");
                }
            }
            else
            {
                throw new JobFailedException($"'{dump.FileName}' is not a pre-sweep dump.");
            }

            logger.LogWarning(
                "Restoring pre-sweep dump '{FileName}' of run '{RunId}' into tenant '{TenantId}' (database " +
                "'{DatabaseName}'), started by '{TriggeredBy}'. The tenant's current data is replaced; secrets " +
                "return to the state before that sweep (possibly plaintext) - run Encrypt afterwards",
                dump.FileName, runId, tenantId, databaseName, triggeredBy ?? "<unknown>");

            // No sweep may run while the tenant database is replaced.
            using (var held = tenantLock?.TryAcquire(tenantId, SecretSweepCoordinator.RestoreLockTimeout))
            {
                if (tenantLock != null && held == null)
                {
                    throw new JobFailedException(
                        $"A secret sweep of tenant '{tenantId}' kept running; the pre-sweep dump was not restored.");
                }

                var result = await systemContext.RestoreTenantAsync(tenantId, databaseName, dumpPath, databaseName,
                    true, true, RestoreTimeout, ct);
                if (!result.Success)
                {
                    throw JobFailedException.CommandExecutionFailed(result, tenantId, "mongorestore");
                }
            }
        }
        finally
        {
            artifactStorage.DeleteScratchFile(scratchPath);
        }

        // The restore is done; a Verify shows what came back (plaintext counts after restoring a pre-Encrypt dump).
        var verify = await secretSweepCoordinator.SweepTenantAsync(tenantId, SecretSweepMode.Verify,
            SecretSweepTrigger.Restore, new SecretSweepRunInfo(performContext?.BackgroundJob?.Id, triggeredBy), ct);
        var legacy = verify.RemainingLegacyValues;
        if (legacy > 0)
        {
            logger.LogWarning(
                "Tenant '{TenantId}' holds {Legacy} legacy (plaintext / enc:v1) secret value(s) after restoring the " +
                "pre-sweep dump of run '{RunId}': run the Encrypt sweep", tenantId, legacy, runId);
        }

        return new PreSweepDumpRestoreResult
        {
            TenantId = tenantId,
            RunId = runId,
            FileName = dump.FileName,
            DatabaseName = databaseName,
            SecretSweep = verify
        };
    }
}

/// <summary>
///     Result of <see cref="IRestorePreSweepDumpJob" /> (job result).
/// </summary>
public class PreSweepDumpRestoreResult
{
    /// <summary>The restored tenant.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The run whose dump was restored.</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>The restored dump.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>The replaced database.</summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>
    ///     The Verify after the restore; <see cref="SecretSweepReport.RemainingLegacyValues" /> &gt; 0 means plaintext
    ///     (or <c>enc:v1</c>) came back - run <c>Encrypt</c>.
    /// </summary>
    public SecretSweepReport? SecretSweep { get; set; }
}
