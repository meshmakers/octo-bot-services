namespace Meshmakers.Octo.Backend.Jobs.Services;

/// <summary>
/// Provides file storage operations for backup files (tus uploads and database dumps).
/// </summary>
public interface IBackupFileStorageService
{
    /// <summary>
    /// Gets the directory tus uploads for a tenant are stored in.
    /// </summary>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <returns>The tenant's upload directory.</returns>
    /// <exception cref="ArgumentException">The tenant id is not usable as a path segment.</exception>
    string GetTusUploadDirectory(string tenantId);

    /// <summary>
    /// Gets the full file path for a tus upload of a tenant.
    /// </summary>
    /// <remarks>
    ///     🔴 <b>The tenant is part of the address, not a cross-check (AB#5060).</b> Uploads are
    ///     stored under a per-tenant directory, so a restore running for one tenant cannot resolve a
    ///     file another tenant staged even if it is handed that file's id. The binding is structural:
    ///     there is no separate ownership check to remember, and no place to forget it. The sink used
    ///     to be flat, with a <c>tenantId</c> upload-metadata field that nothing read — which looked
    ///     like an ownership binding and was not one.
    /// </remarks>
    /// <param name="tenantId">The tenant the upload belongs to.</param>
    /// <param name="tusFileId">The tus file identifier.</param>
    /// <returns>The full path to the uploaded file.</returns>
    /// <exception cref="ArgumentException">The tenant id is not usable as a path segment.</exception>
    string GetTusUploadFilePath(string tenantId, string tusFileId);

    /// <summary>
    /// Gets the full file path for a database dump file.
    /// </summary>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="fileName">The dump file name.</param>
    /// <returns>The full path to the dump file.</returns>
    string GetDumpFilePath(string tenantId, string fileName);

    /// <summary>
    /// Generates a unique dump file name for a tenant.
    /// </summary>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <returns>A unique file name for the dump.</returns>
    string GenerateDumpFileName(string tenantId);

    /// <summary>
    /// Deletes a file at the specified path.
    /// </summary>
    /// <param name="filePath">The path to the file to delete.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task DeleteFileAsync(string filePath);

    /// <summary>
    /// Cleans up stale files that are older than the specified retention period.
    /// </summary>
    /// <param name="retention">The retention period. Files older than this will be deleted.</param>
    /// <returns>The number of files deleted.</returns>
    Task<int> CleanupStaleFilesAsync(TimeSpan retention);

    /// <summary>
    /// Ensures that the required storage directories exist.
    /// </summary>
    void EnsureDirectoriesExist();

    /// <summary>
    /// Gets the configured tus storage path.
    /// </summary>
    string TusStoragePath { get; }

    /// <summary>
    /// Gets the configured dump storage path.
    /// </summary>
    string DumpStoragePath { get; }

    /// <summary>
    /// Gets the directory of the pre-sweep secret backups (AB#5539, decision 10). These dumps hold Secret
    /// values as they were before the secret sweep - possibly clear text - and are secret material: they live
    /// outside the tus and dump directories, are never offered as a job download, are owner-only on Unix and
    /// are deleted by <see cref="CleanupStaleSecretBackupsAsync" /> after their own retention (7 days), not
    /// by <see cref="CleanupStaleFilesAsync" />.
    /// </summary>
    string SecretBackupStoragePath { get; }

    /// <summary>
    /// Creates the tenant's secret backup directory (owner-only on Unix) and returns the path of a new,
    /// not yet existing pre-sweep dump file (<c>&lt;tenant&gt;-&lt;utc&gt;-&lt;guid&gt;.presweep.tar.gz</c>).
    /// </summary>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <returns>The full path of the dump file to write.</returns>
    /// <exception cref="ArgumentException">The tenant id is not usable as a path segment.</exception>
    string CreateSecretBackupFilePath(string tenantId);

    /// <summary>
    /// Restricts a file to its owner (Unix mode 0600; no-op on Windows).
    /// </summary>
    /// <param name="filePath">The file.</param>
    void RestrictToOwner(string filePath);

    /// <summary>
    /// Deletes pre-sweep secret backups older than <paramref name="retention" />.
    /// </summary>
    /// <param name="retention">The retention period.</param>
    /// <returns>The number of files deleted.</returns>
    Task<int> CleanupStaleSecretBackupsAsync(TimeSpan retention);
}
