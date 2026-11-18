using MongoDB.Bson;

namespace Meshmakers.Octo.Backend.Jobs.IndexUpgrade;

/// <summary>
///     A CK Ascending index in the legacy shape that the paging tiebreak upgrade replaces (AB#8960, AB#9118).
/// </summary>
/// <param name="CollectionName">The runtime-entity collection that holds the index</param>
/// <param name="IndexName">The index name (legacy <c>&lt;Type&gt;_&lt;n&gt;</c>, or any other name)</param>
/// <param name="Keys">The key paths in index order</param>
public sealed record LegacyIndex(string CollectionName, string IndexName, IReadOnlyList<string> Keys)
{
    /// <summary>The keys as one readable string, e.g. <c>{ckTypeId, attributes.name, rtState}</c>.</summary>
    public string KeysText => "{" + string.Join(", ", Keys) + "}";
}

/// <summary>
///     Decides from the index <b>keys</b> whether an index of a runtime-entity collection is a CK Ascending index in the
///     legacy shape <c>{ckTypeId, attr…, rtState}</c> that <c>ITenantContext.UpgradeTiebreakIndexesAsync</c> rebuilds as
///     <c>{ckTypeId, attr…, _id, rtState}</c> (AB#9118). The index name is never looked at: an upgraded index may carry
///     a hashed name <c>{Type}_{n}_id_&lt;hash&gt;</c>, and only the keys tell the shape.
/// </summary>
/// <remarks>
///     The engine builds every CK Ascending index with <c>ckTypeId</c> first and <c>rtState</c> among the keys
///     (MongoDbRepositoryDataSource, AB#8960); the tiebreak shape adds <c>_id</c> right before <c>rtState</c>. So a legacy
///     index is: not unique, not a text index (<c>_fts</c>), first key <c>ckTypeId</c>, an <c>rtState</c> key, and no
///     <c>_id</c> key. This leaves out by their keys alone the system-level indexes the engine keeps as they are
///     (<c>{rtState}</c>, <c>{ckTypeId, rtCreatedBy}</c>), the System Entity index <c>{ckTypeId, _id, rtState}</c>,
///     unique, UniqueNotDeleted and text indexes.
/// </remarks>
public static class LegacyIndexShape
{
    internal const string CkTypeIdKey = "ckTypeId";
    internal const string RtStateKey = "rtState";
    internal const string IdKey = "_id";
    private const string TextKey = "_fts";

    /// <summary>
    ///     True when <paramref name="index" /> (a document of <c>listIndexes</c>) is a CK Ascending index in the legacy
    ///     shape.
    /// </summary>
    public static bool IsLegacy(BsonDocument index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (!index.TryGetValue("key", out var keyValue) || keyValue is not BsonDocument key || key.ElementCount < 2)
        {
            return false;
        }

        if (index.TryGetValue("unique", out var unique) && unique.ToBoolean())
        {
            return false;
        }

        var names = key.Names.ToList();
        return names[0] == CkTypeIdKey &&
               names.Contains(RtStateKey) &&
               !names.Contains(IdKey) &&
               !names.Contains(TextKey);
    }

    /// <summary>
    ///     The keys the engine builds for a legacy index: <c>_id</c> inserted right before the first <c>rtState</c>
    ///     (engine <c>TiebreakIndexShape.WithIdTiebreak</c>).
    /// </summary>
    public static IReadOnlyList<string> TiebreakKeysOf(IReadOnlyList<string> legacyKeys)
    {
        var result = legacyKeys.ToList();
        var rtState = result.IndexOf(RtStateKey);
        result.Insert(rtState < 0 ? result.Count : rtState, IdKey);
        return result;
    }

    /// <summary>The key paths of an index document of <c>listIndexes</c>, in index order.</summary>
    public static IReadOnlyList<string> KeysOf(BsonDocument index) =>
        index.TryGetValue("key", out var key) && key is BsonDocument document ? document.Names.ToList() : [];
}
