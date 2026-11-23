using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.Jobs.IndexUpgrade;

/// <summary>How the last index upgrade run of a tenant ended (AB#9118).</summary>
public enum IndexUpgradeRunStatus
{
    /// <summary>The run builds now.</summary>
    Running,

    /// <summary>
    ///     Every legacy index the engine manages was replaced. Leftovers the job cannot change (AB#9322) are named in
    ///     <see cref="IndexUpgradeRunRecord.Warning" /> and <see cref="IndexUpgradeRunRecord.Leftovers" />.
    /// </summary>
    Succeeded,

    /// <summary>The tenant had no legacy index; nothing was built.</summary>
    NothingToDo,

    /// <summary>
    ///     The run stopped before its next collection or did not start (window closed or off, yielded to a CK import,
    ///     global limit, <c>index_update_lock</c> held); the rest follows in a later run.
    /// </summary>
    Deferred,

    /// <summary>The service stopped during the run; Hangfire runs the job again.</summary>
    Interrupted,

    /// <summary>
    ///     The engine ran, but legacy indexes of collections it manages (collection roots of Available models) are left,
    ///     e.g. an index build the engine logged as failed, or what is left could not be read or classified. Listed in
    ///     <see cref="IndexUpgradeRunRecord.Error" />; a retry would change nothing, the next sweep tries again. Leftovers
    ///     the job can never change (models not Available, orphaned collections) do not make a run Incomplete
    ///     (AB#9322): it ends <see cref="Succeeded" /> with <see cref="IndexUpgradeRunRecord.Warning" />.
    /// </summary>
    Incomplete,

    /// <summary>The run failed with <see cref="IndexUpgradeRunRecord.Error" />.</summary>
    Failed
}

/// <summary>
///     The last index upgrade run of a tenant, in the system database (collection <c>IndexUpgradeRuns</c>, one document
///     per tenant). Status only: nothing reads it to decide whether to build; the index keys decide.
/// </summary>
public sealed class IndexUpgradeRunRecord
{
    /// <summary>The tenant id (lower case), the document id.</summary>
    [BsonId]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The Hangfire job of the run.</summary>
    public string? JobId { get; set; }

    /// <summary>What started the run: <c>tenant-created</c>, <c>after-deploy</c>, <c>window</c>, <c>manual</c>.</summary>
    public string? Trigger { get; set; }

    /// <summary>The status.</summary>
    [BsonRepresentation(BsonType.String)]
    public IndexUpgradeRunStatus Status { get; set; }

    /// <summary>When the run started (UTC).</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>When the run ended (UTC); null while running.</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>Duration in milliseconds; null while running.</summary>
    public long? DurationMs { get; set; }

    /// <summary>Legacy indexes found before the run; null when the run ended before it looked.</summary>
    public int? LegacyIndexesBefore { get; set; }

    /// <summary>Legacy indexes left after the run; null when they were not read (or the final read failed).</summary>
    public int? LegacyIndexesAfter { get; set; }

    /// <summary>The collections whose legacy indexes the run replaced.</summary>
    public List<string> CollectionsUpgraded { get; set; } = [];

    /// <summary>Why the run was deferred or failed, or (Incomplete) which managed legacy indexes are left.</summary>
    public string? Error { get; set; }

    /// <summary>
    ///     The legacy indexes left by category (AB#9322): after the run, or before it when nothing was for the engine;
    ///     null when the run ended before it looked or what was left could not be classified. Absent in records of older
    ///     versions.
    /// </summary>
    public IndexUpgradeLeftovers? Leftovers { get; set; }

    /// <summary>
    ///     The legacy indexes the job cannot upgrade (models not Available, orphaned collections) as text (AB#9322), set
    ///     whenever a classified run has such leftovers, so a success with leftovers is never silent. The same entries are
    ///     in <see cref="Leftovers" /> as fields.
    /// </summary>
    public string? Warning { get; set; }
}

/// <summary>The legacy indexes left after an index upgrade run, by category (AB#9322, <see cref="LegacyIndexCategory" />).</summary>
public sealed class IndexUpgradeLeftovers
{
    /// <summary>Legacy indexes of collection roots of Available models (the engine should have upgraded them).</summary>
    public int ManagedIndexes { get; set; }

    /// <summary>Legacy indexes of collection roots of models that are not Available.</summary>
    public int ModelNotAvailableIndexes { get; set; }

    /// <summary>Legacy indexes of collections without a collection root in the CK metadata.</summary>
    public int OrphanedIndexes { get; set; }

    /// <summary>The models that are not Available and hold legacy indexes, ordered by model id.</summary>
    public List<IndexUpgradeModelNotAvailable> ModelsNotAvailable { get; set; } = [];

    /// <summary>
    ///     The orphaned collections with legacy indexes, ordered by name, at most
    ///     <see cref="LegacyIndexClassification.OrphanedCollectionsCap" />; <see cref="OrphanedCollectionsTotal" /> counts
    ///     all.
    /// </summary>
    public List<string> OrphanedCollections { get; set; } = [];

    /// <summary>How many orphaned collections hold legacy indexes.</summary>
    public int OrphanedCollectionsTotal { get; set; }
}

/// <summary>A model that is not Available and holds legacy indexes (AB#9322).</summary>
public sealed class IndexUpgradeModelNotAvailable
{
    /// <summary>The model id, e.g. <c>Monitoring-2.1.0</c>.</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>The model state as text, e.g. <c>ResolveFailed</c> or <c>Importing</c>.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>The legacy indexes of its collection roots.</summary>
    public int IndexCount { get; set; }
}

/// <summary>Writes and reads the last-run record of the index upgrade per tenant (AB#9118).</summary>
public interface IIndexUpgradeRunStore
{
    /// <summary>Replaces the record of <see cref="IndexUpgradeRunRecord.TenantId" />.</summary>
    Task SaveAsync(IndexUpgradeRunRecord record, CancellationToken cancellationToken = default);

    /// <summary>The record of a tenant, null when none was written.</summary>
    Task<IndexUpgradeRunRecord?> GetAsync(string tenantId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
internal sealed class MongoIndexUpgradeRunStore(IIndexUpgradeMongoClient client) : IIndexUpgradeRunStore
{
    internal const string CollectionName = "IndexUpgradeRuns";

    public async Task SaveAsync(IndexUpgradeRunRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.TenantId = record.TenantId.Trim().ToLowerInvariant();
        await Collection().ReplaceOneAsync(r => r.TenantId == record.TenantId, record,
            new ReplaceOptions { IsUpsert = true }, cancellationToken);
    }

    public async Task<IndexUpgradeRunRecord?> GetAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var key = tenantId.Trim().ToLowerInvariant();
        return await Collection().Find(r => r.TenantId == key).FirstOrDefaultAsync(cancellationToken);
    }

    private IMongoCollection<IndexUpgradeRunRecord> Collection() =>
        client.GetSystemDatabase().GetCollection<IndexUpgradeRunRecord>(CollectionName);
}
