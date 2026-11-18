namespace Meshmakers.Octo.Backend.Jobs.DumpRegistry;

/// <summary>
///     The dumps this platform produced: one entry per tenant and SHA-256 of the file exactly as the download serves it.
///     Written only by the dump job and the backfill; no API writes to it.
/// </summary>
public interface IDumpHashRegistry
{
    /// <summary>Adds the entry, or renews it when the tenant already has an entry with the same hash.</summary>
    Task RegisterAsync(DumpHashEntry entry, CancellationToken cancellationToken = default);

    /// <summary>The entry of <paramref name="tenantId" /> with <paramref name="sha256" />, or <c>null</c>.</summary>
    Task<DumpHashEntry?> FindAsync(string tenantId, string sha256, CancellationToken cancellationToken = default);

    /// <summary>Whether an entry of <paramref name="tenantId" /> was written for the stored file <paramref name="fileName" />.</summary>
    Task<bool> ContainsFileAsync(string tenantId, string fileName, CancellationToken cancellationToken = default);

    /// <summary>Removes every entry of <paramref name="tenantId" />.</summary>
    /// <returns>The number of removed entries.</returns>
    Task<long> DeleteTenantAsync(string tenantId, CancellationToken cancellationToken = default);

    /// <summary>Removes the entries of <paramref name="tenantId" /> dumped before <paramref name="dumpedBeforeUtc" />.</summary>
    /// <returns>The number of removed entries.</returns>
    Task<long> DeleteTenantDumpedBeforeAsync(string tenantId, DateTime dumpedBeforeUtc,
        CancellationToken cancellationToken = default);
}
