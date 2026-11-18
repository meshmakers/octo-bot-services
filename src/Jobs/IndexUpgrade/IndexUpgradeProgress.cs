using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.IndexUpgrade;

/// <summary>
///     Follows the collections of one index upgrade run (AB#9118): the engine upgrades a whole tenant in one call, so
///     the job looks at the legacy indexes of the collections still pending and logs each collection once its legacy
///     indexes are gone - tenant, collection, legacy and new index names and keys, and when the change was seen. The time
///     is the time since the previous observation that confirmed a replacement, shared by every collection found in the
///     same observation (the engine works through the collections one after the other; precision: the poll interval).
/// </summary>
internal sealed class IndexUpgradeProgress
{
    private readonly ITenantIndexInspector _inspector;
    private readonly ILogger _logger;
    private readonly string _tenantId;
    private readonly string _databaseName;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, List<LegacyIndex>> _pending;
    private readonly List<string> _done = [];
    private long _lastDetection;

    public IndexUpgradeProgress(ITenantIndexInspector inspector, ILogger logger, string tenantId, string databaseName,
        IEnumerable<LegacyIndex> legacyIndexes, TimeProvider? timeProvider = null)
    {
        _inspector = inspector;
        _logger = logger;
        _tenantId = tenantId;
        _databaseName = databaseName;
        _time = timeProvider ?? TimeProvider.System;
        _lastDetection = _time.GetTimestamp();
        _pending = legacyIndexes.GroupBy(i => i.CollectionName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
    }

    /// <summary>
    ///     The collections whose legacy indexes are gone and whose replacements were seen, in the order they were seen.
    ///     A collection whose legacy indexes are gone without a replacement (e.g. dropped meanwhile) is not counted.
    /// </summary>
    public IReadOnlyList<string> Done => _done;

    /// <summary>
    ///     Looks at the pending collections once and logs the ones that are done. Never throws for a read failure (the
    ///     log is diagnostics; the run's result is decided by the final check).
    /// </summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        IReadOnlyList<LegacyIndex> stillLegacy;
        try
        {
            stillLegacy = await _inspector.FindLegacyIndexesAsync(_databaseName, _pending.Keys.ToList(),
                cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Index upgrade of tenant '{TenantId}': could not read the progress", _tenantId);
            return;
        }

        var stillPending = stillLegacy.Select(i => i.CollectionName).ToHashSet(StringComparer.Ordinal);
        var finished = _pending.Keys.Where(c => !stillPending.Contains(c)).OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
        if (finished.Count == 0)
        {
            return;
        }

        // One elapsed value per observation; the reference point moves only when a replacement was confirmed.
        var now = _time.GetTimestamp();
        var sinceLast = (long)_time.GetElapsedTime(_lastDetection, now).TotalMilliseconds;
        var confirmed = false;
        foreach (var collection in finished)
        {
            var replaced = await LogDoneAsync(collection, _pending[collection], sinceLast, cancellationToken);
            if (replaced == null)
            {
                // The index list could not be read: look again with the next observation.
                continue;
            }

            if (replaced.Value)
            {
                _done.Add(collection);
                confirmed = true;
            }

            _pending.Remove(collection);
        }

        if (confirmed)
        {
            _lastDetection = now;
        }
    }

    /// <summary>
    ///     Logs the replacements of a collection; true when at least one replacement was seen, null when the index list
    ///     could not be read.
    /// </summary>
    private async Task<bool?> LogDoneAsync(string collection, List<LegacyIndex> legacy, long sinceLastMs,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<(string Name, IReadOnlyList<string> Keys)> current;
        try
        {
            current = await _inspector.ListIndexesAsync(_databaseName, collection, cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Index upgrade of tenant '{TenantId}': could not list the indexes of '{Collection}'",
                _tenantId, collection);
            return null;
        }

        var replaced = false;
        foreach (var index in legacy)
        {
            // The engine builds CK Ascending indexes only, so the keys in order identify the replacement.
            var expected = LegacyIndexShape.TiebreakKeysOf(index.Keys);
            var built = current.FirstOrDefault(i => i.Keys.SequenceEqual(expected, StringComparer.Ordinal));
            replaced |= built.Name != null;
            _logger.LogInformation(
                "Index upgrade of tenant '{TenantId}': collection '{Collection}' index '{LegacyIndexName}' {LegacyKeys} " +
                "replaced by '{NewIndexName}' {NewKeys} (seen {SinceLastMs} ms after the previous collection)",
                _tenantId, collection, index.IndexName, index.KeysText, built.Name ?? "(not found)",
                "{" + string.Join(", ", expected) + "}", sinceLastMs);
        }

        return replaced;
    }
}
