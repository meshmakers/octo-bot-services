using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Services.ArtifactStorage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.Jobs.DumpRegistry;

/// <summary>
///     Registers a stored dump: hashes the file exactly as the download serves it (the plaintext of an encrypted
///     artifact) and writes the entry. Called only after the dump is stored. The entry is dated by the start of the dump,
///     so a dump that began before a tenant with the same id was created again never counts for the new tenant. A failed
///     registry write is tried again a few times; the stored dump is not taken again.
/// </summary>
public sealed class DumpHashRecorder(
    IDumpHashRegistry registry,
    IOptions<DumpHashRegistryOptions> options,
    ILogger<DumpHashRecorder> logger,
    IBotArtifactStorage? artifactStorage = null)
{
    private const int RegistryWriteAttempts = 3;
    private static readonly TimeSpan RegistryWriteBackoff = TimeSpan.FromMilliseconds(500);

    /// <summary>Registers the dump the job result <paramref name="jobResult" /> names.</summary>
    /// <param name="tenantId">The tenant the dump was taken of.</param>
    /// <param name="jobResult">The job result: an artifact reference or a local path.</param>
    /// <param name="jobId">The dump job, when known.</param>
    /// <param name="dumpStartedAtUtc">When the dump started, before any data was read.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The SHA-256 of the dump, lower-case hex.</returns>
    /// <exception cref="JobFailedException">The dump cannot be read.</exception>
    public async Task<string> RecordAsync(string tenantId, string jobResult, string? jobId,
        DateTime dumpStartedAtUtc, CancellationToken cancellationToken)
    {
        var (sha256, sizeBytes, fileName) = await HashJobResultAsync(tenantId, jobResult, cancellationToken);
        await RegisterAsync(tenantId, sha256, dumpStartedAtUtc, fileName, sizeBytes, jobId, DumpHashSources.Dump,
            cancellationToken);
        logger.LogInformation("Dump '{FileName}' of tenant '{TenantId}' registered with hash {HashPrefix}",
            fileName, tenantId, Sha256OfContent.Prefix(sha256));
        return sha256;
    }

    /// <summary>Registers a stored tenant dump of an earlier version, dated by the earliest time it can have started.</summary>
    /// <returns>The SHA-256 of the dump, lower-case hex.</returns>
    public async Task<string> RecordStoredDumpAsync(string tenantId, string fileName, DateTime earliestStartUtc,
        CancellationToken cancellationToken)
    {
        var (sha256, sizeBytes) = await HashStoredDumpAsync(tenantId, fileName, cancellationToken);
        await RegisterAsync(tenantId, sha256, earliestStartUtc, fileName, sizeBytes, null, DumpHashSources.Backfill,
            cancellationToken);
        return sha256;
    }

    private async Task RegisterAsync(string tenantId, string sha256, DateTime dumpedAtUtc, string fileName,
        long sizeBytes, string? jobId, string source, CancellationToken cancellationToken)
    {
        var entry = new DumpHashEntry(tenantId.ToLowerInvariant(), sha256, dumpedAtUtc,
            dumpedAtUtc + options.Value.Retention, fileName, sizeBytes, jobId, source);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await registry.RegisterAsync(entry, cancellationToken);
                return;
            }
            catch (Exception exception) when (attempt < RegistryWriteAttempts &&
                                              exception is not OperationCanceledException)
            {
                logger.LogWarning(exception,
                    "Registering dump '{FileName}' of tenant '{TenantId}' failed (attempt {Attempt}); trying again",
                    fileName, tenantId, attempt);
                await Task.Delay(RegistryWriteBackoff * attempt, cancellationToken);
            }
        }
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
