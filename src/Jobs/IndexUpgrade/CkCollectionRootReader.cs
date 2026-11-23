using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.Jobs.IndexUpgrade;

/// <summary>
///     A collection root of the tenant's CK metadata: a CK type with <c>IsCollectionRoot</c>, the runtime-entity
///     collection it owns and the state of its model (AB#9322).
/// </summary>
/// <param name="CollectionName">The runtime-entity collection, e.g. <c>RtEntity_MonitoringMonitoredTenant</c></param>
/// <param name="CkTypeId">The CK type id as stored, e.g. <c>Monitoring-2.1.0/MonitoredTenant-1</c></param>
/// <param name="ModelId">The CK model id, e.g. <c>Monitoring-2.1.0</c></param>
/// <param name="State">The state of the model the type belongs to</param>
public sealed record CkCollectionRoot(string CollectionName, string CkTypeId, string ModelId, ModelState State);

/// <summary>
///     Reads the collection roots of a tenant database with the state of their models (AB#9322), so the job can tell
///     which legacy indexes the engine upgrades (collection roots of <c>Available</c> models) and which it never touches.
///     Read-only.
/// </summary>
/// <remarks>
///     The engine's public CK type queries (<c>ITenantRepository.GetCkTypeAsync</c>) return types of <c>Available</c>
///     models only, so a model in another state could not be named. This reader therefore reads the engine's CK metadata
///     collection <c>CkType</c> directly - a coupling to the engine's storage schema (collection <c>CkType</c>, fields
///     <c>_id</c>, <c>ckModelId</c>, <c>modelState</c>, <c>isCollectionRoot</c>) that is kept in this one class. The
///     integration test <c>IndexUpgradeIntegrationTests.CollectionRootReader_ReadsWhatTheEngineWrote</c> creates the
///     metadata through the real engine, so a schema change there fails that test instead of production. The collection
///     name is derived as the engine derives it: <c>RtEntity_</c> + <see cref="RtCkIdExtensions.ToStorageIdentifier{TKey}(RtCkId{TKey})" />
///     of the type id.
/// </remarks>
public interface ICkCollectionRootReader
{
    /// <summary>The collection roots of <paramref name="databaseName" />, of models in any state.</summary>
    Task<IReadOnlyList<CkCollectionRoot>> GetCollectionRootsAsync(string databaseName,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
internal sealed class CkCollectionRootReader(IIndexUpgradeMongoClient client) : ICkCollectionRootReader
{
    /// <summary>The engine's CK type collection (<c>CkTypeMongoDataSourceMapper.CollectionNamePrefix</c>).</summary>
    internal const string CkTypeCollectionName = "CkType";

    /// <summary>The engine's prefix of runtime-entity collections.</summary>
    internal const string RtEntityCollectionPrefix = "RtEntity_";

    private const string ModelIdField = "ckModelId";
    private const string ModelStateField = "modelState";
    private const string IsCollectionRootField = "isCollectionRoot";

    public async Task<IReadOnlyList<CkCollectionRoot>> GetCollectionRootsAsync(string databaseName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        var collection = client.GetDatabase(databaseName).GetCollection<BsonDocument>(CkTypeCollectionName);
        var documents = await collection
            .Find(new BsonDocument(IsCollectionRootField, true))
            .Project(new BsonDocument { { "_id", 1 }, { ModelIdField, 1 }, { ModelStateField, 1 } })
            .ToListAsync(cancellationToken);
        return documents.Select(ToRoot).ToList();
    }

    /// <summary>Maps one projected <c>CkType</c> document; an unreadable document fails the read (no guess).</summary>
    internal static CkCollectionRoot ToRoot(BsonDocument document)
    {
        var typeId = document["_id"].AsString;
        var modelId = document.TryGetValue(ModelIdField, out var model) && model.IsString
            ? model.AsString
            : new CkId<CkTypeId>(typeId).ModelId.ToString();
        return new CkCollectionRoot(CollectionNameOf(typeId), typeId, modelId, StateOf(document));
    }

    /// <summary>The runtime-entity collection of a collection root, as the engine names it.</summary>
    internal static string CollectionNameOf(string ckTypeId) =>
        RtEntityCollectionPrefix + new CkId<CkTypeId>(ckTypeId).ToRtCkId().ToStorageIdentifier();

    private static ModelState StateOf(BsonDocument document)
    {
        if (!document.TryGetValue(ModelStateField, out var value))
        {
            // The engine writes the enum's default (Importing = 0) like any other value; a missing field is unknown.
            throw new FormatException($"CK type '{document["_id"]}' has no {ModelStateField}");
        }

        if (value.IsInt32 || value.IsInt64)
        {
            var number = value.ToInt32();
            if (Enum.IsDefined(typeof(ModelState), number))
            {
                return (ModelState)number;
            }
        }
        else if (value.IsString && Enum.TryParse<ModelState>(value.AsString, true, out var parsed) &&
                 Enum.IsDefined(parsed))
        {
            // TryParse also accepts a numeric string such as "7" that names no member.
            return parsed;
        }

        throw new FormatException($"CK type '{document["_id"]}' has an unknown {ModelStateField} '{value}'");
    }
}
