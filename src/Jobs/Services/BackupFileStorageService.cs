using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Services;

/// <summary>
/// Provides file storage operations for backup files (tus uploads and database dumps).
/// </summary>
public class BackupFileStorageService : IBackupFileStorageService
{
    private readonly ILogger<BackupFileStorageService> _logger;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="tusStoragePath">The storage path for tus uploads.</param>
    /// <param name="dumpStoragePath">The storage path for database dumps.</param>
    /// <param name="logger">The logger instance.</param>
    public BackupFileStorageService(string tusStoragePath, string dumpStoragePath,
        ILogger<BackupFileStorageService> logger)
    {
        TusStoragePath = tusStoragePath;
        DumpStoragePath = dumpStoragePath;
        _logger = logger;
    }

    /// <inheritdoc />
    public string TusStoragePath { get; }

    /// <inheritdoc />
    public string DumpStoragePath { get; }

    /// <inheritdoc />
    public string GetTusUploadFilePath(string tusFileId)
    {
        return Path.Combine(TusStoragePath, tusFileId);
    }

    /// <inheritdoc />
    public string GetDumpFilePath(string tenantId, string fileName)
    {
        return Path.Combine(DumpStoragePath, tenantId, fileName);
    }

    /// <inheritdoc />
    public string GenerateDumpFileName(string tenantId)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var guid = Guid.NewGuid().ToString("N")[..8];
        return $"{tenantId}-{timestamp}-{guid}.tar.gz";
    }

    /// <inheritdoc />
    public Task DeleteFileAsync(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogInformation("Deleted backup file '{FilePath}'", filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete backup file '{FilePath}'", filePath);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> CleanupStaleFilesAsync(TimeSpan retention)
    {
        var deletedCount = 0;
        var cutoff = DateTime.UtcNow - retention;

        deletedCount += CleanupDirectory(TusStoragePath, cutoff);
        deletedCount += CleanupDirectory(DumpStoragePath, cutoff);

        if (deletedCount > 0)
        {
            _logger.LogInformation("Cleaned up {Count} stale backup files older than {Retention}", deletedCount,
                retention);
        }

        return Task.FromResult(deletedCount);
    }

    /// <inheritdoc />
    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(TusStoragePath);
        Directory.CreateDirectory(DumpStoragePath);
        _logger.LogInformation("Ensured backup storage directories exist: '{TusPath}', '{DumpPath}'",
            TusStoragePath, DumpStoragePath);
    }

    private int CleanupDirectory(string directoryPath, DateTime cutoff)
    {
        var deletedCount = 0;

        if (!Directory.Exists(directoryPath))
        {
            return deletedCount;
        }

        foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                var lastWriteTime = File.GetLastWriteTimeUtc(file);
                if (lastWriteTime < cutoff)
                {
                    File.Delete(file);
                    deletedCount++;
                    _logger.LogDebug("Deleted stale file '{FilePath}' (last modified: {LastWrite})", file,
                        lastWriteTime);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete stale file '{FilePath}'", file);
            }
        }

        // Clean up empty subdirectories
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(directoryPath))
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up empty directories in '{DirectoryPath}'", directoryPath);
        }

        return deletedCount;
    }
}
