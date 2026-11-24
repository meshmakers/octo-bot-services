using Meshmakers.Octo.Backend.Jobs.IndexUpgrade;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

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
///     plays the engine for one collection. Also the CK metadata (AB#9322): a collection with a legacy index is the
///     collection root of an Available model unless <see cref="SetRoot" /> or <see cref="MarkOrphaned" /> says otherwise.
/// </summary>
internal sealed class FakeTenantIndexes : ITenantIndexInspector, ICkCollectionRootReader
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<LegacyIndex>> _legacy = new();
    private readonly Dictionary<(string Database, string Collection), CkCollectionRoot?> _roots = new();

    /// <summary>Reads of the CK metadata fail.</summary>
    public bool FailRootReads { get; set; }

    /// <summary>How often the CK metadata was read.</summary>
    public int RootReadCount { get; private set; }

    /// <summary>The collection is the root of a type of <paramref name="modelId" /> in <paramref name="state" />.</summary>
    public void SetRoot(string databaseName, string collection, string modelId, ModelState state)
    {
        lock (_gate)
        {
            _roots[(databaseName, collection)] =
                new CkCollectionRoot(collection, $"{modelId}/{collection}-1", modelId, state);
        }
    }

    /// <summary>
    ///     True when the engine upgrades the collection: it is the root of an Available model (the default for a collection
    ///     without <see cref="SetRoot" /> or <see cref="MarkOrphaned" />).
    /// </summary>
    public bool IsManagedByEngine(string databaseName, string collection)
    {
        lock (_gate)
        {
            return !_roots.TryGetValue((databaseName, collection), out var root) ||
                   root is { State: ModelState.Available };
        }
    }

    /// <summary>The collection has no collection root in the CK metadata.</summary>
    public void MarkOrphaned(string databaseName, string collection)
    {
        lock (_gate)
        {
            _roots[(databaseName, collection)] = null;
        }
    }

    public Task<IReadOnlyList<CkCollectionRoot>> GetCollectionRootsAsync(string databaseName,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            RootReadCount++;
            if (FailRootReads)
            {
                throw new TimeoutException("find CkType timed out");
            }

            var explicitRoots = _roots.Where(r => r.Key.Database == databaseName).ToList();
            var defaults = _legacy.GetValueOrDefault(databaseName, []).Select(i => i.CollectionName)
                .Concat(_upgraded.Where(u => u.Database == databaseName).Select(u => u.Collection))
                .Distinct().Where(c => explicitRoots.All(r => r.Key.Collection != c))
                .Select(c => new CkCollectionRoot(c, $"Test-1.0.0/{c}-1", "Test-1.0.0", ModelState.Available));
            IReadOnlyList<CkCollectionRoot> result = explicitRoots.Where(r => r.Value != null).Select(r => r.Value!)
                .Concat(defaults).ToList();
            return Task.FromResult(result);
        }
    }
    private readonly List<(string Database, string Collection, string Name, IReadOnlyList<string> Keys)> _upgraded = [];

    public int ReadCount { get; private set; }

    /// <summary>Reads of chosen collections (progress, final check) fail; the full listing still works.</summary>
    public bool FailCollectionReads { get; set; }

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
            if (FailCollectionReads && collectionNames != null)
            {
                throw new TimeoutException("listIndexes timed out");
            }

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

/// <summary>Keeps the formatted log lines with their level (AB#9322).</summary>
internal sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    private readonly Lock _gate = new();
    private readonly List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> _entries = [];
    private readonly List<(Microsoft.Extensions.Logging.LogLevel Level, int EventId, string Message)> _events = [];

    /// <summary>The lines with their event id (AB#9709).</summary>
    public IReadOnlyList<(Microsoft.Extensions.Logging.LogLevel Level, int EventId, string Message)> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToList();
            }
        }
    }

    public IReadOnlyList<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    public IReadOnlyList<string> Messages => Entries.Select(e => e.Message).ToList();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            var message = formatter(state, exception);
            _entries.Add((logLevel, message));
            _events.Add((logLevel, eventId.Id, message));
        }
    }
}

/// <summary>A timestamp clock that moves only by <see cref="Advance" /> (AB#9317).</summary>
internal sealed class ManualTimestampProvider : TimeProvider
{
    private long _ticks;

    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
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
                Error = record.Error,
                Leftovers = record.Leftovers,
                Warning = record.Warning
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
