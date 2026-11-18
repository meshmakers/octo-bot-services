using Meshmakers.Octo.Backend.Jobs.DumpRegistry;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.BotServices.Services.DumpRegistry;

/// <summary>
///     <see cref="IDumpHashRegistry" /> in the collection <see cref="CollectionName" /> of the bot's job database: unique
///     per tenant and hash, an index per tenant and file name, and a TTL index that removes an entry when it expires.
/// </summary>
public sealed class MongoDumpHashRegistry : IDumpHashRegistry
{
    /// <summary>The collection of the registry.</summary>
    public const string CollectionName = "DumpHashRegistry";

    private readonly IMongoCollection<DumpHashDocument> _collection;
    private readonly Lazy<Task> _indexes;

    /// <summary>Constructor.</summary>
    /// <param name="jobDatabase">The bot's job database.</param>
    public MongoDumpHashRegistry(IMongoDatabase jobDatabase)
    {
        _collection = jobDatabase.GetCollection<DumpHashDocument>(CollectionName);
        _indexes = new Lazy<Task>(CreateIndexesAsync);
    }

    /// <inheritdoc />
    public async Task RegisterAsync(DumpHashEntry entry, CancellationToken cancellationToken = default)
    {
        await _indexes.Value;
        var tenantKey = TenantKey(entry.TenantId);
        var update = Builders<DumpHashDocument>.Update
            .Set(document => document.DumpedAtUtc, entry.DumpedAtUtc)
            .Set(document => document.ExpiresAtUtc, entry.ExpiresAtUtc)
            .Set(document => document.FileName, entry.FileName)
            .Set(document => document.SizeBytes, entry.SizeBytes)
            .Set(document => document.JobId, entry.JobId)
            .Set(document => document.Source, entry.Source);
        await _collection.UpdateOneAsync(TenantAndHash(tenantKey, entry.Sha256), update,
            new UpdateOptions { IsUpsert = true }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DumpHashEntry?> FindAsync(string tenantId, string sha256,
        CancellationToken cancellationToken = default)
    {
        var document = await _collection.Find(TenantAndHash(TenantKey(tenantId), sha256))
            .FirstOrDefaultAsync(cancellationToken);
        return document?.ToEntry();
    }

    /// <inheritdoc />
    public async Task<bool> ContainsFileAsync(string tenantId, string fileName,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<DumpHashDocument>.Filter.Eq(document => document.TenantId, TenantKey(tenantId)) &
                     Builders<DumpHashDocument>.Filter.Eq(document => document.FileName, fileName);
        return await _collection.Find(filter).AnyAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<long> DeleteTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var filter = Builders<DumpHashDocument>.Filter.Eq(document => document.TenantId, TenantKey(tenantId));
        var result = await _collection.DeleteManyAsync(filter, cancellationToken);
        return result.DeletedCount;
    }

    /// <inheritdoc />
    public async Task<long> DeleteTenantDumpedBeforeAsync(string tenantId, DateTime dumpedBeforeUtc,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<DumpHashDocument>.Filter.Eq(document => document.TenantId, TenantKey(tenantId)) &
                     Builders<DumpHashDocument>.Filter.Lt(document => document.DumpedAtUtc, dumpedBeforeUtc);
        var result = await _collection.DeleteManyAsync(filter, cancellationToken);
        return result.DeletedCount;
    }

    private static string TenantKey(string tenantId) => tenantId.ToLowerInvariant();

    private static FilterDefinition<DumpHashDocument> TenantAndHash(string tenantKey, string sha256) =>
        Builders<DumpHashDocument>.Filter.Eq(document => document.TenantId, tenantKey) &
        Builders<DumpHashDocument>.Filter.Eq(document => document.Sha256, sha256);

    private Task CreateIndexesAsync()
    {
        var keys = Builders<DumpHashDocument>.IndexKeys;
        var tenantAndHash = new CreateIndexModel<DumpHashDocument>(
            keys.Ascending(document => document.TenantId).Ascending(document => document.Sha256),
            new CreateIndexOptions { Unique = true, Name = "tenantId_sha256" });
        var tenantAndFile = new CreateIndexModel<DumpHashDocument>(
            keys.Ascending(document => document.TenantId).Ascending(document => document.FileName),
            new CreateIndexOptions { Name = "tenantId_fileName" });
        var expiry = new CreateIndexModel<DumpHashDocument>(keys.Ascending(document => document.ExpiresAtUtc),
            new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Name = "expiresAtUtc_ttl" });
        return _collection.Indexes.CreateManyAsync([tenantAndHash, tenantAndFile, expiry]);
    }

    internal sealed class DumpHashDocument
    {
        [BsonId]
        public ObjectId Id { get; set; }

        [BsonElement("tenantId")]
        public string TenantId { get; set; } = string.Empty;

        [BsonElement("sha256")]
        public string Sha256 { get; set; } = string.Empty;

        [BsonElement("dumpedAtUtc")]
        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime DumpedAtUtc { get; set; }

        [BsonElement("expiresAtUtc")]
        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime ExpiresAtUtc { get; set; }

        [BsonElement("fileName")]
        public string FileName { get; set; } = string.Empty;

        [BsonElement("sizeBytes")]
        public long SizeBytes { get; set; }

        [BsonElement("jobId")]
        public string? JobId { get; set; }

        [BsonElement("source")]
        public string Source { get; set; } = string.Empty;

        public DumpHashEntry ToEntry() =>
            new(TenantId, Sha256, DumpedAtUtc, ExpiresAtUtc, FileName, SizeBytes, JobId, Source);
    }
}
