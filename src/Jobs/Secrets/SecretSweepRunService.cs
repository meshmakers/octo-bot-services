using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.ArtifactStorage;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Secrets;

/// <inheritdoc />
/// <remarks>
///     Dumps named <c>*.presweep.octoenc</c> live in the artifact store (AB#5561); legacy <c>*.presweep.tar.gz</c>
///     dumps written before it live on the local disk (<c>Bot:SecretSweep:BackupStoragePath</c>) until they expire.
/// </remarks>
public class SecretSweepRunService(
    ISecretSweepRunStore runStore,
    IBackupFileStorageService backupFileStorage,
    ILogger<SecretSweepRunService> logger,
    TimeProvider? timeProvider = null,
    IBotArtifactStorage? artifactStorage = null) : ISecretSweepRunService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SecretSweepRunDto>> GetRunsAsync(string tenantId, int limit)
    {
        var runs = await runStore.GetRunsAsync(tenantId, limit);
        foreach (var dump in runs.Select(r => r.Dump).OfType<SecretSweepDumpDto>())
        {
            await RefreshFileStateAsync(tenantId, dump);
        }

        return runs;
    }

    /// <inheritdoc />
    public async Task<SecretSweepDumpDeleteResultDto> DeleteDumpAsync(string tenantId, string runId,
        string? deletedBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (string.IsNullOrWhiteSpace(runId))
        {
            return SecretSweepDumpDeleteResultDto.NotFound;
        }

        // Decided and recorded under the store's per-tenant lock, so two concurrent deletes answer 204 once
        // and 409 once.
        var result = SecretSweepDumpDeleteResultDto.NotFound;
        string? fileName = null;
        var deletedAt = _time.GetUtcNow().UtcDateTime;
        await runStore.UpdateAsync(tenantId, runId, run =>
        {
            if (run.Dump == null)
            {
                result = SecretSweepDumpDeleteResultDto.NotFound;
                return false;
            }

            if (run.Dump.DeletedAt != null)
            {
                result = SecretSweepDumpDeleteResultDto.AlreadyDeleted;
                return false;
            }

            fileName = run.Dump.FileName;
            run.Dump.DeletedAt = deletedAt;
            run.Dump.DeletedBy = deletedBy;
            run.Dump.Exists = false;
            result = SecretSweepDumpDeleteResultDto.Deleted;
            return true;
        });

        if (result == SecretSweepDumpDeleteResultDto.Deleted && fileName != null)
        {
            var stillThere = await DeleteDumpFileAsync(tenantId, fileName);
            if (stillThere)
            {
                // Recorded as deleted already; the hourly cleanup removes it at the latest on expiry (and the
                // lifecycle rule of the store after that).
                logger.LogError("Pre-sweep secret backup '{FileName}' of tenant '{TenantId}' (run '{RunId}') was " +
                                "marked deleted but could not be removed", fileName, tenantId, runId);
            }
            else
            {
                logger.LogInformation("Pre-sweep secret backup '{FileName}' of tenant '{TenantId}' (run '{RunId}') " +
                                      "deleted early by '{DeletedBy}'", fileName, tenantId, runId,
                    deletedBy ?? "<unknown>");
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<SecretSweepDumpRestoreCheck> CheckDumpRestorableAsync(string tenantId, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (string.IsNullOrWhiteSpace(runId))
        {
            return SecretSweepDumpRestoreCheck.Of(SecretSweepDumpRestoreState.NotFound);
        }

        var run = (await runStore.GetRunsAsync(tenantId)).FirstOrDefault(r => r.RunId == runId);
        var dump = run?.Dump;
        if (dump == null)
        {
            return SecretSweepDumpRestoreCheck.Of(SecretSweepDumpRestoreState.NotFound);
        }

        if (dump.DeletedAt != null)
        {
            return SecretSweepDumpRestoreCheck.Of(SecretSweepDumpRestoreState.Deleted, dump.FileName);
        }

        try
        {
            if (PreSweepDumps.IsStoredDump(dump.FileName) && artifactStorage != null)
            {
                var info = await artifactStorage.GetInfoAsync(ArtifactCategories.Presweep, tenantId, dump.FileName);
                if (info == null)
                {
                    return SecretSweepDumpRestoreCheck.Of(SecretSweepDumpRestoreState.Missing, dump.FileName);
                }

                var header = await artifactStorage.ReadHeaderAsync(ArtifactCategories.Presweep, tenantId,
                    dump.FileName);
                if (header != null && !artifactStorage.CanUnprotect(header))
                {
                    return new SecretSweepDumpRestoreCheck(SecretSweepDumpRestoreState.KeyMissing, dump.FileName,
                        header.KeyId);
                }

                return SecretSweepDumpRestoreCheck.Of(SecretSweepDumpRestoreState.Restorable, dump.FileName);
            }

            if (PreSweepDumps.IsLegacyDump(dump.FileName) &&
                File.Exists(backupFileStorage.GetSecretBackupFilePath(tenantId, dump.FileName)))
            {
                return SecretSweepDumpRestoreCheck.Of(SecretSweepDumpRestoreState.Restorable, dump.FileName);
            }
        }
        catch (ArgumentException)
        {
            // A file name that does not fit the layout cannot be restored.
        }

        return SecretSweepDumpRestoreCheck.Of(SecretSweepDumpRestoreState.Missing, dump.FileName);
    }

    /// <summary>
    ///     Deletes the dump file; returns <c>true</c> when it still exists afterwards.
    /// </summary>
    private async Task<bool> DeleteDumpFileAsync(string tenantId, string fileName)
    {
        if (PreSweepDumps.IsStoredDump(fileName) && artifactStorage != null)
        {
            try
            {
                await artifactStorage.DeleteAsync(ArtifactCategories.Presweep, tenantId, fileName);
                return await artifactStorage.GetInfoAsync(ArtifactCategories.Presweep, tenantId, fileName) != null;
            }
            catch (Exception e) when (e is not ArgumentException)
            {
                logger.LogWarning(e, "Could not delete pre-sweep secret backup '{FileName}' of tenant '{TenantId}' " +
                                     "from the artifact store", fileName, tenantId);
                return true;
            }
        }

        var path = backupFileStorage.GetSecretBackupFilePath(tenantId, fileName);
        await backupFileStorage.DeleteFileAsync(path);
        return File.Exists(path);
    }

    private async Task RefreshFileStateAsync(string tenantId, SecretSweepDumpDto dump)
    {
        try
        {
            if (PreSweepDumps.IsStoredDump(dump.FileName))
            {
                var info = artifactStorage == null || dump.DeletedAt != null
                    ? null
                    : await artifactStorage.GetInfoAsync(ArtifactCategories.Presweep, tenantId, dump.FileName);
                dump.Exists = info != null;
                if (info != null)
                {
                    dump.SizeBytes = info.Size;
                }

                return;
            }

            var file = new FileInfo(backupFileStorage.GetSecretBackupFilePath(tenantId, dump.FileName));
            dump.Exists = dump.DeletedAt == null && file.Exists;
            if (file.Exists)
            {
                dump.SizeBytes = file.Length;
            }
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            dump.Exists = false;
        }
        catch (Exception e)
        {
            // The store is unreachable: the run list still answers, the dump state is unknown.
            logger.LogWarning(e, "Could not read the state of pre-sweep secret backup '{FileName}' of tenant " +
                                 "'{TenantId}'", dump.FileName, tenantId);
            dump.Exists = false;
        }
    }
}

/// <summary>
///     Whether a run's pre-sweep dump can be restored (AB#5559).
/// </summary>
public enum SecretSweepDumpRestoreState
{
    /// <summary>The dump exists and its key id is in the key ring.</summary>
    Restorable = 0,

    /// <summary>Unknown run, or the run has no dump.</summary>
    NotFound = 1,

    /// <summary>The dump was deleted (early or expired).</summary>
    Deleted = 2,

    /// <summary>The run records a dump, but it is not in the store (any more).</summary>
    Missing = 3,

    /// <summary>The dump is encrypted with a key id that is not in the key ring.</summary>
    KeyMissing = 4
}

/// <summary>
///     Result of <see cref="ISecretSweepRunService.CheckDumpRestorableAsync" />.
/// </summary>
/// <param name="State">The state.</param>
/// <param name="FileName">The dump file name, when the run has one.</param>
/// <param name="KeyId">The missing key id for <see cref="SecretSweepDumpRestoreState.KeyMissing" />.</param>
public sealed record SecretSweepDumpRestoreCheck(
    SecretSweepDumpRestoreState State,
    string? FileName = null,
    string? KeyId = null)
{
    internal static SecretSweepDumpRestoreCheck Of(SecretSweepDumpRestoreState state, string? fileName = null)
    {
        return new SecretSweepDumpRestoreCheck(state, fileName);
    }
}
