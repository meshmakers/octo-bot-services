using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.Jobs.IndexUpgrade;

/// <summary>How the last index upgrade run of a tenant ended (AB#9118).</summary>
public enum IndexUpgradeRunStatus
{
    /// <summary>The run builds now.</summary>
    Running,

    /// <summary>Every legacy index of the tenant was replaced.</summary>
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

    /// <summary>Legacy indexes found before the run.</summary>
    public int LegacyIndexesBefore { get; set; }

    /// <summary>Legacy indexes left after the run.</summary>
    public int? LegacyIndexesAfter { get; set; }

    /// <summary>The collections whose legacy indexes the run replaced.</summary>
    public List<string> CollectionsUpgraded { get; set; } = [];

    /// <summary>Why the run was deferred or failed.</summary>
    public string? Error { get; set; }
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
