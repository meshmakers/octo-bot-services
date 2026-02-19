namespace Meshmakers.Octo.Backend.Jobs.Services;

/// <summary>
/// Provides file storage operations for backup files (tus uploads and database dumps).
/// </summary>
public interface IBackupFileStorageService
{
    /// <summary>
    /// Gets the full file path for a tus upload by its file ID.
    /// </summary>
    /// <param name="tusFileId">The tus file identifier.</param>
    /// <returns>The full path to the uploaded file.</returns>
    string GetTusUploadFilePath(string tusFileId);

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
}
