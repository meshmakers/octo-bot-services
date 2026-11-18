using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Services.ArtifactStorage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.Jobs.DumpRegistry;

/// <summary>
///     Registers a stored dump: hashes the file exactly as the download serves it (the plaintext of an encrypted
///     artifact) and writes the entry. Called only after the dump is stored.
/// </summary>
public sealed class DumpHashRecorder(
    IDumpHashRegistry registry,
    IOptions<DumpHashRegistryOptions> options,
    TimeProvider timeProvider,
    ILogger<DumpHashRecorder> logger,
    IBotArtifactStorage? artifactStorage = null)
{
    /// <summary>Registers the dump the job result <paramref name="jobResult" /> names.</summary>
    /// <returns>The SHA-256 of the dump, lower-case hex.</returns>
    /// <exception cref="JobFailedException">The dump cannot be read.</exception>
    public async Task<string> RecordAsync(string tenantId, string jobResult, string? jobId,
        CancellationToken cancellationToken)
    {
        var (sha256, sizeBytes, fileName) = await HashJobResultAsync(tenantId, jobResult, cancellationToken);
        var dumpedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        await RegisterAsync(tenantId, sha256, dumpedAtUtc, fileName, sizeBytes, jobId, DumpHashSources.Dump,
            cancellationToken);
        logger.LogInformation("Dump '{FileName}' of tenant '{TenantId}' registered with hash {HashPrefix}",
            fileName, tenantId, Sha256OfContent.Prefix(sha256));
        return sha256;
    }

    /// <summary>Registers a stored tenant dump of an earlier version, dated by its artifact.</summary>
    /// <returns>The SHA-256 of the dump, lower-case hex.</returns>
    public async Task<string> RecordStoredDumpAsync(string tenantId, string fileName, DateTime storedAtUtc,
        CancellationToken cancellationToken)
    {
        var (sha256, sizeBytes) = await HashStoredDumpAsync(tenantId, fileName, cancellationToken);
        await RegisterAsync(tenantId, sha256, storedAtUtc, fileName, sizeBytes, null, DumpHashSources.Backfill,
            cancellationToken);
        return sha256;
    }

    private Task RegisterAsync(string tenantId, string sha256, DateTime dumpedAtUtc, string fileName, long sizeBytes,
        string? jobId, string source, CancellationToken cancellationToken)
    {
        var entry = new DumpHashEntry(tenantId.ToLowerInvariant(), sha256, dumpedAtUtc,
            dumpedAtUtc + options.Value.Retention, fileName, sizeBytes, jobId, source);
        return registry.RegisterAsync(entry, cancellationToken);
    }

    private async Task<(string Sha256, long SizeBytes, string FileName)> HashJobResultAsync(string tenantId,
        string jobResult, CancellationToken cancellationToken)
    {
        if (!StoredArtifact.TryParseResultReference(jobResult, out var artifact) || artifact == null)
        {
            var localSha256 = await Sha256OfContent.OfFileAsync(jobResult, cancellationToken);
            return (localSha256, new FileInfo(jobResult).Length, Path.GetFileName(jobResult));
        }

        var isTenantDumpOfThisTenant = artifact.Category == ArtifactCategories.TenantDumps &&
                                       string.Equals(artifact.TenantId, tenantId, StringComparison.OrdinalIgnoreCase);
        if (!isTenantDumpOfThisTenant)
        {
            throw new JobFailedException(
                $"The dump of tenant '{tenantId}' was not stored as a tenant dump of that tenant.");
        }

        var (sha256, sizeBytes) = await HashStoredDumpAsync(tenantId, artifact.FileName, cancellationToken);
        return (sha256, sizeBytes, artifact.FileName);
    }

    private async Task<(string Sha256, long SizeBytes)> HashStoredDumpAsync(string tenantId, string fileName,
        CancellationToken cancellationToken)
    {
        if (artifactStorage == null)
        {
            throw new JobFailedException($"No artifact store is wired to read the dump of tenant '{tenantId}'.");
        }

        await using var download = await artifactStorage.OpenDownloadAsync(ArtifactCategories.TenantDumps, tenantId,
                                       fileName, cancellationToken) ??
                                   throw new JobFailedException(
                                       $"The dump '{fileName}' of tenant '{tenantId}' is not in the artifact store.");
        await using var hashingSink = new HashingWriteStream();
        await artifactStorage.CopyPlainAsync(download, hashingSink, cancellationToken);
        return (hashingSink.HashHex(), hashingSink.BytesWritten);
    }
}
