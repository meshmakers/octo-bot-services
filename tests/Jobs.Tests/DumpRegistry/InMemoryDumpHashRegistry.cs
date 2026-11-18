using System.Collections.Concurrent;
using Meshmakers.Octo.Backend.Jobs.DumpRegistry;

namespace Meshmakers.Octo.Backend.Jobs.Tests.DumpRegistry;

internal sealed class InMemoryDumpHashRegistry : IDumpHashRegistry
{
    private readonly ConcurrentDictionary<(string Tenant, string Sha256), DumpHashEntry> _entries = new();

    public IReadOnlyCollection<DumpHashEntry> Entries => _entries.Values.ToList();

    public int FailingRegistrations { get; set; }

    public Task RegisterAsync(DumpHashEntry entry, CancellationToken cancellationToken = default)
    {
        if (FailingRegistrations > 0)
        {
            FailingRegistrations--;
            throw new InvalidOperationException("registry not reachable");
        }

        var normalizedEntry = entry with { TenantId = entry.TenantId.ToLowerInvariant() };
        _entries[(normalizedEntry.TenantId, entry.Sha256)] = normalizedEntry;
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
        var matchingEntries = _entries.Where(pair => pair.Key.Tenant == tenantKey && predicate(pair.Value)).ToList();
        var removedCount = 0L;
        foreach (var matchingEntry in matchingEntries)
        {
            if (_entries.TryRemove(matchingEntry))
            {
                removedCount++;
            }
        }

        return removedCount;
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
