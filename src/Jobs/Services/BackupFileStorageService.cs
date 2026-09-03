using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Services;

/// <summary>
/// Provides file storage operations for backup files (tus uploads and database dumps).
/// </summary>
public class BackupFileStorageService : IBackupFileStorageService
{
    /// <summary>
    ///     What may become a per-tenant directory name. Deliberately narrower than "a tenant id the
    ///     platform accepts": this is a filesystem guard, so it admits only characters that mean the
    ///     same thing on every platform we run on, and no separator, no dot-segment, no whitespace.
    /// </summary>
    private static readonly Regex TenantDirectoryName =
        new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled);

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
    public string GetTusUploadDirectory(string tenantId)
    {
        return ResolveTenantDirectory(TusStoragePath, tenantId);
    }

    /// <inheritdoc />
    public string GetTusUploadFilePath(string tenantId, string tusFileId)
    {
        return Path.Combine(GetTusUploadDirectory(tenantId), tusFileId);
    }

    /// <inheritdoc />
    public string GetDumpFilePath(string tenantId, string fileName)
    {
        return Path.Combine(ResolveTenantDirectory(DumpStoragePath, tenantId), fileName);
    }

    /// <summary>
    ///     Resolves the per-tenant subdirectory of a storage root, rejecting any tenant id that
    ///     would not stay inside it.
    /// </summary>
    /// <remarks>
    ///     🔴 <b>The tenant id reaching this method is caller-supplied, and this class does not get to
    ///     assume who checked it.</b> For tus uploads it arrives as a route value, where the shared
    ///     <c>TenantIdRouteConstraint</c> already rejects anything that cannot name a tenant — but for
    ///     dumps it arrives as a Hangfire job argument, deserialized from storage, on no route at all.
    ///     One path is guarded upstream and the other is not, so the guard belongs here, at the point
    ///     where a string becomes a directory.
    ///     <para>
    ///         Two independent checks, because either alone is a single point of failure: the tenant
    ///         id must look like a tenant id, and the combined path must still sit under the root.
    ///         The second catches whatever the first fails to anticipate — platform-specific path
    ///         quirks, an absolute path, a device name — and it is the check that cannot be argued
    ///         around, since it compares the resolved result rather than the input.
    ///     </para>
    /// </remarks>
    /// <param name="root">The storage root.</param>
    /// <param name="tenantId">The tenant the files belong to.</param>
    /// <returns>The tenant's directory beneath <paramref name="root" />.</returns>
    /// <exception cref="ArgumentException">The tenant id is not usable as a path segment.</exception>
    private static string ResolveTenantDirectory(string root, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || !TenantDirectoryName.IsMatch(tenantId))
        {
            throw new ArgumentException(
                $"'{tenantId}' is not a valid tenant id for file storage.", nameof(tenantId));
        }

        var rootFull = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(rootFull, tenantId));

        // TrimEnd so a root of "/data/tus" is not treated as a prefix of "/data/tus-other".
        var rootPrefix = rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"'{tenantId}' would resolve outside the storage root.", nameof(tenantId));
        }

        return combined;
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
