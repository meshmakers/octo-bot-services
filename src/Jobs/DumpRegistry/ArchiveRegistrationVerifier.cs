using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.DumpRegistry;

/// <summary>
///     Admits the archive of a restore by a tenant identity only when its SHA-256 is registered for the target tenant
///     and the entry has not expired. Fails closed: without a registry every archive is refused.
/// </summary>
public sealed class ArchiveRegistrationVerifier(
    IDumpHashRegistry? registry,
    TimeProvider timeProvider,
    ILogger<ArchiveRegistrationVerifier> logger)
{
    /// <summary>Hashes the file at <paramref name="archivePath" /> and checks it against the registry.</summary>
    /// <exception cref="ArchiveNotRegisteredException">The archive is not a registered dump of the tenant.</exception>
    public async Task VerifyAsync(string tenantId, string archivePath, CancellationToken cancellationToken)
    {
        var sha256 = await Sha256OfContent.OfFileAsync(archivePath, cancellationToken);
        if (registry == null)
        {
            logger.LogError(
                "Restore of tenant '{TenantId}' refused: no dump registry is wired, so no archive can be checked",
                tenantId);
            throw new ArchiveNotRegisteredException();
        }

        var entry = await registry.FindAsync(tenantId.ToLowerInvariant(), sha256, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (entry == null || entry.ExpiresAtUtc <= now)
        {
            logger.LogWarning(
                "Restore of tenant '{TenantId}' refused: archive with hash {HashPrefix} is not a registered dump of the tenant",
                tenantId, Sha256OfContent.Prefix(sha256));
            throw new ArchiveNotRegisteredException();
        }

        logger.LogInformation(
            "Archive with hash {HashPrefix} is a registered dump of tenant '{TenantId}' from {DumpedAtUtc:O}",
            Sha256OfContent.Prefix(sha256), tenantId, entry.DumpedAtUtc);
    }
}
