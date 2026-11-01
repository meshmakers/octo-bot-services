using System.Text.RegularExpressions;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Engine.StreamData;

namespace Meshmakers.Octo.Backend.Jobs.Commands;

/// <summary>
///     AB#8049 — refuses a stored query whose own field filter or sort order reaches a CK v2 <c>Hidden</c> attribute
///     (e.g. <c>System.Identity/User.PasswordHash</c>) before the export job runs it. The repository applies such a
///     filter (it does not know <c>access</c>), and although the exported entities never carry a Hidden value, the
///     <i>set</i> of exported rows answers "does the stored value equal / start with my guess" — an oracle. The asset
///     repository refuses to store such a query, but a query can also arrive through an import, a restore or an older
///     version, so the job checks it itself.
/// </summary>
/// <remarks>
///     Same rule as the asset repository's <c>AccessQueryGuard</c> and the mesh adapter's
///     <c>RtAttributeAccessGuard.EnsureStoredQueryNotHidden</c>: a plain or record path is resolved against the query
///     type and every type derived from it (<see cref="ArchiveHiddenColumnGuard.FindHiddenAttribute" />, the engine's
///     path rule); a path that crosses a navigation (<c>-&gt;</c>), an entity-selector key (<c>[key=value]</c>) and a
///     query type the CK cache does not know are checked by the trailing attribute name against every type of the
///     tenant (conservative, fail closed). The error names the query, the path (selector values removed) and the
///     hidden attribute, never a comparison value.
/// </remarks>
internal static partial class StoredQueryAccessGuard
{
    internal const string FieldFilterUsage = "field filter";
    internal const string SortOrderUsage = "sort order";

    [GeneratedRegex(@"\[([^\[\]=]+)=[^\[\]]*\]")]
    private static partial Regex EntitySelectorRegex();

    /// <summary>Throws when one of the paths reaches a Hidden attribute. The tenant CK cache must be loaded.</summary>
    /// <exception cref="CommandExecutionFailedException">A stored path reaches a hidden attribute</exception>
    public static void EnsureNotHidden(ICkCacheService ckCacheService, string tenantId, OctoObjectId queryId,
        RtCkId<CkTypeId> queryCkTypeId, IEnumerable<(string? Path, string Usage)> paths)
    {
        ArgumentNullException.ThrowIfNull(ckCacheService);
        IReadOnlyList<RtCkId<CkTypeId>>? queryTypes = null;
        IReadOnlyList<RtCkId<CkTypeId>>? allTypes = null;

        foreach (var (path, usage) in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            queryTypes ??= QueryTypes(ckCacheService, tenantId, queryCkTypeId);
            allTypes ??= ckCacheService.GetCkTypes(tenantId).Select(t => t.CkTypeId.ToRtCkId()).ToList();

            var hidden = FindHidden(ckCacheService, tenantId, queryTypes, allTypes, path);
            if (hidden != null)
            {
                throw CommandExecutionFailedException.HiddenAttributeInStoredQuery(queryId, usage,
                    WithoutSelectorValues(path), hidden);
            }
        }
    }

    private static string? FindHidden(ICkCacheService cache, string tenantId,
        IReadOnlyList<RtCkId<CkTypeId>> queryTypes, IReadOnlyList<RtCkId<CkTypeId>> allTypes, string path)
    {
        foreach (Match match in EntitySelectorRegex().Matches(path))
        {
            // The key may be a record path (Credentials.Token): check it as written and by its trailing attribute.
            var key = match.Groups[1].Value.Trim();
            var hiddenKey = FindInTypes(cache, tenantId, allTypes, key) ??
                            FindInTypes(cache, tenantId, allTypes, LastName(key));
            if (hiddenKey != null)
            {
                return hiddenKey;
            }
        }

        var plain = EntitySelectorRegex().Replace(path, string.Empty);
        var navigationIndex = plain.LastIndexOf("->", StringComparison.Ordinal);
        if (navigationIndex >= 0)
        {
            var tail = plain[(navigationIndex + 2)..];
            return tail.Contains("::", StringComparison.Ordinal) ? null : FindInTypes(cache, tenantId, allTypes, tail);
        }

        return plain.Contains("::", StringComparison.Ordinal) ? null : FindInTypes(cache, tenantId, queryTypes, plain);
    }

    private static string? FindInTypes(ICkCacheService cache, string tenantId, IEnumerable<RtCkId<CkTypeId>> types,
        string path) =>
        path.Length == 0
            ? null
            : types.Select(t => ArchiveHiddenColumnGuard.FindHiddenAttribute(cache, tenantId, t, path))
                .FirstOrDefault(h => h != null);

    /// <summary>
    ///     The query type and every type derived from it; every type of the tenant when the cache does not know the
    ///     query type (fail closed).
    /// </summary>
    private static IReadOnlyList<RtCkId<CkTypeId>> QueryTypes(ICkCacheService cache, string tenantId,
        RtCkId<CkTypeId> queryCkTypeId)
    {
        if (cache.TryGetRtCkType(tenantId, queryCkTypeId, out var graph) && graph != null)
        {
            return graph.GetAllDerivedTypes(true).Select(t => t.ToRtCkId()).Distinct().ToList();
        }

        return cache.GetCkTypes(tenantId).Select(t => t.CkTypeId.ToRtCkId()).ToList();
    }

    private static string LastName(string key)
    {
        var trimmed = key.Trim();
        var lastDot = trimmed.LastIndexOf('.');
        return lastDot >= 0 ? trimmed[(lastDot + 1)..] : trimmed;
    }

    /// <summary>
    ///     The path with the values of its entity selectors removed (<c>[key=…]</c>), for error messages: everything from
    ///     the first <c>=</c> inside a selector up to the last <c>]</c> of the path is replaced, so a value that itself
    ///     contains <c>]</c> cannot leak a part of it.
    /// </summary>
    internal static string WithoutSelectorValues(string path)
    {
        var open = path.IndexOf('[');
        var equals = open < 0 ? -1 : path.IndexOf('=', open);
        var close = path.LastIndexOf(']');
        return equals < 0 || close < equals ? path : path[..equals] + "=…" + path[close..];
    }
}
