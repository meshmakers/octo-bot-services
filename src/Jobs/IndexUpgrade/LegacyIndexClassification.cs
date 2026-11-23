using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.Jobs.IndexUpgrade;

/// <summary>
///     Why a legacy index is there (AB#9322), decided from the tenant's CK metadata (collection roots and the state of
///     their models), never from the index name.
/// </summary>
public enum LegacyIndexCategory
{
    /// <summary>A collection root of an <c>Available</c> model: the engine upgrades it.</summary>
    Managed,

    /// <summary>A collection root of a model in another state (<c>ResolveFailed</c>, <c>Importing</c>): the engine skips it.</summary>
    ModelNotAvailable,

    /// <summary>A collection without a collection root in the CK metadata (left by an older model or a migration).</summary>
    Orphaned
}

/// <summary>A legacy index of a collection root whose model is not <c>Available</c>.</summary>
/// <param name="Index">The index</param>
/// <param name="ModelId">The model id, e.g. <c>Monitoring-2.1.0</c></param>
/// <param name="State">The model state</param>
public sealed record NotAvailableIndex(LegacyIndex Index, string ModelId, ModelState State);

/// <summary>
///     The legacy indexes of a tenant, each in exactly one <see cref="LegacyIndexCategory" /> (AB#9322).
/// </summary>
public sealed class LegacyIndexClassification
{
    /// <summary>How many orphaned collection names the record and the warning list at most.</summary>
    public const int OrphanedCollectionsCap = 20;

    private LegacyIndexClassification(IReadOnlyList<LegacyIndex> managed, IReadOnlyList<NotAvailableIndex> notAvailable,
        IReadOnlyList<LegacyIndex> orphaned)
    {
        Managed = managed;
        NotAvailable = notAvailable;
        Orphaned = orphaned;
    }

    /// <summary>Legacy indexes of collection roots of <c>Available</c> models.</summary>
    public IReadOnlyList<LegacyIndex> Managed { get; }

    /// <summary>Legacy indexes of collection roots of models that are not <c>Available</c>.</summary>
    public IReadOnlyList<NotAvailableIndex> NotAvailable { get; }

    /// <summary>Legacy indexes of collections without a collection root.</summary>
    public IReadOnlyList<LegacyIndex> Orphaned { get; }

    /// <summary>True when the engine has something to upgrade.</summary>
    public bool HasManaged => Managed.Count > 0;

    /// <summary>True when legacy indexes are there that no run of the job can change.</summary>
    public bool HasLeftovers => NotAvailable.Count > 0 || Orphaned.Count > 0;

    /// <summary>
    ///     Classifies <paramref name="legacy" /> by <paramref name="roots" />: a collection with a root of an
    ///     <c>Available</c> model is managed (also when another, older root of it is not Available); one whose roots are
    ///     all of models in another state is "model not Available" (the first root by model id names the model); one
    ///     without a root is orphaned.
    /// </summary>
    public static LegacyIndexClassification Classify(IReadOnlyList<LegacyIndex> legacy,
        IReadOnlyList<CkCollectionRoot> roots)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(roots);
        var byCollection = roots.GroupBy(r => r.CollectionName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.State == ModelState.Available ? 0 : 1)
                .ThenBy(r => r.ModelId, StringComparer.Ordinal).First(), StringComparer.Ordinal);

        var managed = new List<LegacyIndex>();
        var notAvailable = new List<NotAvailableIndex>();
        var orphaned = new List<LegacyIndex>();
        foreach (var index in legacy)
        {
            if (!byCollection.TryGetValue(index.CollectionName, out var root))
            {
                orphaned.Add(index);
            }
            else if (root.State == ModelState.Available)
            {
                managed.Add(index);
            }
            else
            {
                notAvailable.Add(new NotAvailableIndex(index, root.ModelId, root.State));
            }
        }

        return new LegacyIndexClassification(managed, notAvailable, orphaned);
    }

    /// <summary>Reads the collection roots of <paramref name="databaseName" /> and classifies <paramref name="legacy" />.</summary>
    public static async Task<LegacyIndexClassification> ClassifyAsync(ICkCollectionRootReader reader,
        string databaseName, IReadOnlyList<LegacyIndex> legacy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return Classify(legacy, await reader.GetCollectionRootsAsync(databaseName, cancellationToken));
    }

    /// <summary>The structured form for the run record (<see cref="IndexUpgradeRunRecord.Leftovers" />).</summary>
    public IndexUpgradeLeftovers ToRecord()
    {
        var orphanedCollections = OrphanedCollectionNames();
        return new IndexUpgradeLeftovers
        {
            ManagedIndexes = Managed.Count,
            ModelNotAvailableIndexes = NotAvailable.Count,
            OrphanedIndexes = Orphaned.Count,
            ModelsNotAvailable = ModelsNotAvailable().Select(m => new IndexUpgradeModelNotAvailable
                { ModelId = m.ModelId, State = m.State.ToString(), IndexCount = m.IndexCount }).ToList(),
            OrphanedCollections = orphanedCollections.Take(OrphanedCollectionsCap).ToList(),
            OrphanedCollectionsTotal = orphanedCollections.Count
        };
    }

    /// <summary>
    ///     The warning for the leftovers no run can change, e.g. <c>model not Available: Monitoring-2.1.0 ResolveFailed
    ///     (4 indexes); orphaned (no collection root): RtEntity_SystemQuery (2 indexes)</c>; null when there are none.
    /// </summary>
    public string? Warning()
    {
        if (!HasLeftovers)
        {
            return null;
        }

        var parts = new List<string>();
        var models = ModelsNotAvailable();
        if (models.Count > 0)
        {
            parts.Add("model not Available: " +
                      string.Join(", ", models.Select(m => $"{m.ModelId} {m.State} ({Indexes(m.IndexCount)})")));
        }

        if (Orphaned.Count > 0)
        {
            var collections = Orphaned.GroupBy(i => i.CollectionName, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
            var listed = string.Join(", ", collections.Take(OrphanedCollectionsCap)
                .Select(g => $"{g.Key} ({Indexes(g.Count())})"));
            var more = collections.Count > OrphanedCollectionsCap
                ? $" and {collections.Count - OrphanedCollectionsCap} more collection(s)"
                : "";
            parts.Add($"orphaned (no collection root): {listed}{more}");
        }

        return $"{NotAvailable.Count + Orphaned.Count} legacy index(es) are left that the job cannot upgrade; " +
               string.Join("; ", parts);
    }

    private List<(string ModelId, ModelState State, int IndexCount)> ModelsNotAvailable() =>
        NotAvailable.GroupBy(i => (i.ModelId, i.State))
            .Select(g => (g.Key.ModelId, g.Key.State, g.Count()))
            .OrderBy(m => m.ModelId, StringComparer.Ordinal).ToList();

    private List<string> OrphanedCollectionNames() =>
        Orphaned.Select(i => i.CollectionName).Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal).ToList();

    private static string Indexes(int count) => count == 1 ? "1 index" : $"{count} indexes";
}
