using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.IndexUpgrade;

/// <summary>
///     Follows the collections of one index upgrade run (AB#9118): the engine upgrades a whole tenant in one call, so
///     the job looks at the legacy indexes of the collections still pending and logs each collection once its legacy
///     indexes are gone - tenant, collection, legacy and new index names and keys, and the duration since the previous
///     collection finished (the engine works through the collections one after the other; precision: the poll interval).
/// </summary>
internal sealed class IndexUpgradeProgress
{
    private readonly ITenantIndexInspector _inspector;
    private readonly ILogger _logger;
    private readonly string _tenantId;
    private readonly string _databaseName;
    private readonly Dictionary<string, List<LegacyIndex>> _pending;
    private readonly List<string> _done = [];
    private readonly Stopwatch _sinceLastCompletion = Stopwatch.StartNew();

    public IndexUpgradeProgress(ITenantIndexInspector inspector, ILogger logger, string tenantId, string databaseName,
        IEnumerable<LegacyIndex> legacyIndexes)
    {
        _inspector = inspector;
        _logger = logger;
        _tenantId = tenantId;
        _databaseName = databaseName;
        _pending = legacyIndexes.GroupBy(i => i.CollectionName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
    }

    /// <summary>The collections whose legacy indexes are gone, in the order they were seen.</summary>
    public IReadOnlyList<string> Done => _done;

    /// <summary>The legacy indexes of the collections not seen done yet.</summary>
    public IReadOnlyList<LegacyIndex> Remaining => _pending.Values.SelectMany(i => i).ToList();

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
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogDebug(e, "Index upgrade of tenant '{TenantId}': could not read the progress", _tenantId);
            return;
        }

        var stillPending = stillLegacy.Select(i => i.CollectionName).ToHashSet(StringComparer.Ordinal);
        foreach (var collection in _pending.Keys.Where(c => !stillPending.Contains(c)).OrderBy(c => c).ToList())
        {
            await LogDoneAsync(collection, _pending[collection], cancellationToken);
            _pending.Remove(collection);
            _done.Add(collection);
            _sinceLastCompletion.Restart();
        }
    }

    private async Task LogDoneAsync(string collection, List<LegacyIndex> legacy, CancellationToken cancellationToken)
    {
        IReadOnlyList<(string Name, IReadOnlyList<string> Keys)> current = [];
        try
        {
            current = await _inspector.ListIndexesAsync(_databaseName, collection, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogDebug(e, "Index upgrade of tenant '{TenantId}': could not list the indexes of '{Collection}'",
                _tenantId, collection);
        }

        foreach (var index in legacy)
        {
            var expected = LegacyIndexShape.TiebreakKeysOf(index.Keys);
            var built = current.FirstOrDefault(i => i.Keys.SequenceEqual(expected, StringComparer.Ordinal));
            _logger.LogInformation(
                "Index upgrade of tenant '{TenantId}': collection '{Collection}' index '{LegacyIndexName}' {LegacyKeys} " +
                "replaced by '{NewIndexName}' {NewKeys} ({DurationMs} ms)",
                _tenantId, collection, index.IndexName, index.KeysText, built.Name ?? "(not found)",
                "{" + string.Join(", ", expected) + "}", (long)_sinceLastCompletion.Elapsed.TotalMilliseconds);
        }
    }
}
