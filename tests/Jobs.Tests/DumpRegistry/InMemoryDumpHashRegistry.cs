using System.Collections.Concurrent;
using Meshmakers.Octo.Backend.Jobs.DumpRegistry;

namespace Meshmakers.Octo.Backend.Jobs.Tests.DumpRegistry;

internal sealed class InMemoryDumpHashRegistry : IDumpHashRegistry
{
    private readonly ConcurrentDictionary<(string Tenant, string Sha256), DumpHashEntry> _entries = new();

    public IReadOnlyCollection<DumpHashEntry> Entries => _entries.Values.ToList();

    public Task RegisterAsync(DumpHashEntry entry, CancellationToken cancellationToken = default)
    {
        _entries[(entry.TenantId.ToLowerInvariant(), entry.Sha256)] = entry;
        return Task.CompletedTask;
    }

    public Task<DumpHashEntry?> FindAsync(string tenantId, string sha256, CancellationToken cancellationToken = default)
    {
        _entries.TryGetValue((tenantId.ToLowerInvariant(), sha256), out var entry);
        return Task.FromResult(entry);
    }

    public Task<bool> ContainsFileAsync(string tenantId, string fileName, CancellationToken cancellationToken = default)
    {
        var tenantKey = tenantId.ToLowerInvariant();
        var contained = _entries.Values.Any(entry => entry.TenantId == tenantKey && entry.FileName == fileName);
        return Task.FromResult(contained);
    }

    public Task<long> DeleteTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(RemoveWhere(tenantId, _ => true));
    }

    public Task<long> DeleteTenantDumpedBeforeAsync(string tenantId, DateTime dumpedBeforeUtc,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(RemoveWhere(tenantId, entry => entry.DumpedAtUtc < dumpedBeforeUtc));
    }

    private long RemoveWhere(string tenantId, Func<DumpHashEntry, bool> predicate)
    {
        var tenantKey = tenantId.ToLowerInvariant();
        var matchingKeys = _entries.Where(pair => pair.Key.Tenant == tenantKey && predicate(pair.Value))
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in matchingKeys)
        {
            _entries.TryRemove(key, out _);
        }

        return matchingKeys.Count;
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
