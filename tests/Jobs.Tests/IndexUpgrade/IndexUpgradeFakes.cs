using Meshmakers.Octo.Backend.Jobs.IndexUpgrade;

namespace Meshmakers.Octo.Backend.Jobs.Tests.IndexUpgrade;

/// <summary>
///     <see cref="IIndexUpgradeCoordination" /> in memory with the semantics of the Hangfire implementation: slots and
///     tenant leases are exclusive, a job counts as active while <see cref="ActiveJobs" /> holds it.
/// </summary>
internal sealed class InMemoryIndexUpgradeCoordination(TimeProvider? timeProvider = null) : IIndexUpgradeCoordination
{
    private readonly Lock _gate = new();
    private readonly HashSet<int> _slots = [];
    private readonly HashSet<string> _tenants = [];
    private readonly Dictionary<string, string> _jobs = new();
    private readonly Dictionary<string, Dictionary<string, DateTime>> _yield = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public HashSet<string> ActiveJobs { get; } = [];
    public DateTime? WindowClosesAt { get; set; }
    public int Enqueued { get; private set; }

    public IDisposable? TryAcquireGlobalSlot(int maxConcurrent)
    {
        lock (_gate)
        {
            for (var slot = 0; slot < maxConcurrent; slot++)
            {
                if (_slots.Add(slot))
                {
                    var held = slot;
                    return new Release(() =>
                    {
                        lock (_gate)
                        {
                            _slots.Remove(held);
                        }
                    });
                }
            }

            return null;
        }
    }

    public IDisposable? TryAcquireTenant(string tenantId, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            lock (_gate)
            {
                if (_tenants.Add(Key(tenantId)))
                {
                    return new Release(() =>
                    {
                        lock (_gate)
                        {
                            _tenants.Remove(Key(tenantId));
                        }
                    });
                }
            }

            if (DateTime.UtcNow >= until)
            {
                return null;
            }

            Thread.Sleep(10);
        }
    }

    public (string JobId, bool Created) EnqueueOnce(string tenantId, Func<string> enqueue)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(Key(tenantId), out var existing) && ActiveJobs.Contains(existing))
            {
                return (existing, false);
            }

            var jobId = enqueue();
            _jobs[Key(tenantId)] = jobId;
            ActiveJobs.Add(jobId);
            Enqueued++;
            return (jobId, true);
        }
    }

    public string? GetJobId(string tenantId)
    {
        lock (_gate)
        {
            return _jobs.GetValueOrDefault(Key(tenantId));
        }
    }

    public void RequestYield(string tenantId, string requestId)
    {
        lock (_gate)
        {
            if (!_yield.TryGetValue(Key(tenantId), out var requests))
            {
                _yield[Key(tenantId)] = requests = new Dictionary<string, DateTime>();
            }

            requests[requestId] = _time.GetUtcNow().UtcDateTime;
        }
    }

    public bool IsYieldRequested(string tenantId)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            return _yield.TryGetValue(Key(tenantId), out var requests) &&
                   requests.Values.Any(at => now - at < IIndexUpgradeCoordination.YieldRequestLifetime);
        }
    }

    public void ClearYield(string tenantId, string requestId)
    {
        lock (_gate)
        {
            if (_yield.TryGetValue(Key(tenantId), out var requests))
            {
                requests.Remove(requestId);
            }
        }
    }

    public DateTime? GetWindowClosesAt() => WindowClosesAt;

    public void OpenWindow(DateTime closesAtUtc) => WindowClosesAt = closesAtUtc;

    public bool IsTenantHeld(string tenantId)
    {
        lock (_gate)
        {
            return _tenants.Contains(Key(tenantId));
        }
    }

    private static string Key(string tenantId) => tenantId.Trim().ToLowerInvariant();

    private sealed class Release(Action release) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                release();
            }
        }
    }
}

/// <summary>
///     Index state of fake tenant databases: the legacy indexes per database. <see cref="UpgradeOneCollection" />
///     plays the engine for one collection.
/// </summary>
internal sealed class FakeTenantIndexes : ITenantIndexInspector
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<LegacyIndex>> _legacy = new();
    private readonly List<(string Database, string Collection, string Name, IReadOnlyList<string> Keys)> _upgraded = [];

    public int ReadCount { get; private set; }

    public void AddLegacy(string databaseName, string collection, string indexName, params string[] attributeKeys)
    {
        lock (_gate)
        {
            if (!_legacy.TryGetValue(databaseName, out var list))
            {
                _legacy[databaseName] = list = [];
            }

            list.Add(new LegacyIndex(collection, indexName, ["ckTypeId", .. attributeKeys, "rtState"]));
        }
    }

    public IReadOnlyList<string> PendingCollections(string databaseName)
    {
        lock (_gate)
        {
            return _legacy.GetValueOrDefault(databaseName, []).Select(i => i.CollectionName).Distinct()
                .OrderBy(c => c, StringComparer.Ordinal).ToList();
        }
    }

    public void UpgradeOneCollection(string databaseName, string collection)
    {
        lock (_gate)
        {
            var list = _legacy.GetValueOrDefault(databaseName, []);
            foreach (var index in list.Where(i => i.CollectionName == collection))
            {
                _upgraded.Add((databaseName, collection, index.IndexName + "_id", LegacyIndexShape.TiebreakKeysOf(index.Keys)));
            }

            list.RemoveAll(i => i.CollectionName == collection);
        }
    }

    public Task<IReadOnlyList<LegacyIndex>> FindLegacyIndexesAsync(string databaseName,
        IReadOnlyCollection<string>? collectionNames = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ReadCount++;
            IReadOnlyList<LegacyIndex> result = _legacy.GetValueOrDefault(databaseName, [])
                .Where(i => collectionNames == null || collectionNames.Contains(i.CollectionName)).ToList();
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<(string Name, IReadOnlyList<string> Keys)>> ListIndexesAsync(string databaseName,
        string collectionName, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<(string Name, IReadOnlyList<string> Keys)> result = _upgraded
                .Where(u => u.Database == databaseName && u.Collection == collectionName)
                .Select(u => (u.Name, u.Keys)).ToList();
            return Task.FromResult(result);
        }
    }
}

/// <summary>A clock that stands still at <see cref="Now" /> until a test moves it.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Keeps the last record per tenant and every record written.</summary>
internal sealed class RecordingRunStore : IIndexUpgradeRunStore
{
    private readonly Lock _gate = new();

    public List<(string TenantId, IndexUpgradeRunStatus Status, string? Error)> Written { get; } = [];
    public Dictionary<string, IndexUpgradeRunRecord> Last { get; } = new();

    public Task SaveAsync(IndexUpgradeRunRecord record, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Written.Add((record.TenantId, record.Status, record.Error));
            Last[record.TenantId] = new IndexUpgradeRunRecord
            {
                TenantId = record.TenantId,
                JobId = record.JobId,
                Trigger = record.Trigger,
                Status = record.Status,
                StartedAt = record.StartedAt,
                EndedAt = record.EndedAt,
                DurationMs = record.DurationMs,
                LegacyIndexesBefore = record.LegacyIndexesBefore,
                LegacyIndexesAfter = record.LegacyIndexesAfter,
                CollectionsUpgraded = record.CollectionsUpgraded.ToList(),
                Error = record.Error
            };
        }

        return Task.CompletedTask;
    }

    public Task<IndexUpgradeRunRecord?> GetAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Last.GetValueOrDefault(tenantId));
        }
    }
}
