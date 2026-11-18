using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.Jobs.IndexUpgrade;

/// <summary>
///     Lists the CK Ascending indexes of a tenant database that are still in the legacy shape (AB#9118), by their keys
///     (<see cref="LegacyIndexShape" />). Read-only: <c>listCollections</c> and <c>listIndexes</c>.
/// </summary>
public interface ITenantIndexInspector
{
    /// <summary>
    ///     The legacy indexes of <paramref name="databaseName" />, of every collection or only of
    ///     <paramref name="collectionNames" />; empty when all are upgraded.
    /// </summary>
    Task<IReadOnlyList<LegacyIndex>> FindLegacyIndexesAsync(string databaseName,
        IReadOnlyCollection<string>? collectionNames = null, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The indexes (name and keys) of one collection, without indexes that carry a collation: the engine never
    ///     builds a CK index with one, so such an index is never the replacement of a legacy index.
    /// </summary>
    Task<IReadOnlyList<(string Name, IReadOnlyList<string> Keys)>> ListIndexesAsync(string databaseName,
        string collectionName, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
internal sealed class TenantIndexInspector(IIndexUpgradeMongoClient client) : ITenantIndexInspector
{
    public async Task<IReadOnlyList<LegacyIndex>> FindLegacyIndexesAsync(string databaseName,
        IReadOnlyCollection<string>? collectionNames = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        var database = client.GetDatabase(databaseName);
        var names = collectionNames?.ToList() ?? await ListCollectionNamesAsync(database, cancellationToken);
        var result = new List<LegacyIndex>();
        foreach (var collectionName in names.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var indexes = await ListIndexDocumentsAsync(database, collectionName, cancellationToken);
            result.AddRange(indexes.Where(LegacyIndexShape.IsLegacy)
                .Select(i => new LegacyIndex(collectionName, i["name"].AsString, LegacyIndexShape.KeysOf(i))));
        }

        return result;
    }

    public async Task<IReadOnlyList<(string Name, IReadOnlyList<string> Keys)>> ListIndexesAsync(string databaseName,
        string collectionName, CancellationToken cancellationToken = default)
    {
        var indexes = await ListIndexDocumentsAsync(client.GetDatabase(databaseName), collectionName, cancellationToken);
        return indexes.Where(i => !i.Contains("collation"))
            .Select(i => (i["name"].AsString, LegacyIndexShape.KeysOf(i))).ToList();
    }

    private static async Task<List<string>> ListCollectionNamesAsync(IMongoDatabase database,
        CancellationToken cancellationToken)
    {
        var collectionNames = new List<string>();
        using (var cursor = await database.ListCollectionsAsync(
                   new ListCollectionsOptions { Filter = new BsonDocument("type", "collection") }, cancellationToken))
        {
            foreach (var collection in await cursor.ToListAsync(cancellationToken))
            {
                var name = collection["name"].AsString;
                if (!name.StartsWith("system.", StringComparison.Ordinal))
                {
                    collectionNames.Add(name);
                }
            }
        }

        return collectionNames;
    }

    private static async Task<List<BsonDocument>> ListIndexDocumentsAsync(IMongoDatabase database,
        string collectionName, CancellationToken cancellationToken)
    {
        try
        {
            using var cursor = await database.GetCollection<BsonDocument>(collectionName).Indexes
                .ListAsync(cancellationToken);
            return await cursor.ToListAsync(cancellationToken);
        }
        catch (MongoCommandException e) when (e.CodeName == "NamespaceNotFound")
        {
            // Dropped between listCollections and listIndexes.
            return [];
        }
    }
}
