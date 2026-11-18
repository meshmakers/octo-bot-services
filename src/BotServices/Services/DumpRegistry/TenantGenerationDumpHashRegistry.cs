using Meshmakers.Octo.Backend.Jobs.DumpRegistry;
using Meshmakers.Octo.Services.Methods.Operations;

namespace Meshmakers.Octo.Backend.BotServices.Services.DumpRegistry;

/// <summary>
///     Ignores the entries of an earlier tenant with the same id: a read returns no entry dumped before the current
///     tenant was created. An unknown generation (legacy tenant) does not filter.
/// </summary>
public sealed class TenantGenerationDumpHashRegistry(
    IDumpHashRegistry inner,
    ICkMethodOperationTenantGeneration generation) : IDumpHashRegistry
{
    /// <inheritdoc />
    public Task RegisterAsync(DumpHashEntry entry, CancellationToken cancellationToken = default) =>
        inner.RegisterAsync(entry, cancellationToken);

    /// <inheritdoc />
    public async Task<DumpHashEntry?> FindAsync(string tenantId, string sha256,
        CancellationToken cancellationToken = default)
    {
        var entry = await inner.FindAsync(tenantId, sha256, cancellationToken);
        if (entry == null)
        {
            return null;
        }

        var tenantCreatedUtc = await generation.GetStartUtcAsync(tenantId, cancellationToken);
        var isOfAnEarlierTenant = tenantCreatedUtc is { } createdUtc && entry.DumpedAtUtc < createdUtc;
        return isOfAnEarlierTenant ? null : entry;
    }

    /// <inheritdoc />
    public Task<bool> ContainsFileAsync(string tenantId, string fileName,
        CancellationToken cancellationToken = default) =>
        inner.ContainsFileAsync(tenantId, fileName, cancellationToken);

    /// <inheritdoc />
    public Task<long> DeleteTenantAsync(string tenantId, CancellationToken cancellationToken = default) =>
        inner.DeleteTenantAsync(tenantId, cancellationToken);

    /// <inheritdoc />
    public Task<long> DeleteTenantDumpedBeforeAsync(string tenantId, DateTime dumpedBeforeUtc,
        CancellationToken cancellationToken = default) =>
        inner.DeleteTenantDumpedBeforeAsync(tenantId, dumpedBeforeUtc, cancellationToken);
}
