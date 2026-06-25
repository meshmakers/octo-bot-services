using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;

/// <summary>
///     Maps an engine <see cref="ArchiveSnapshot"/> (read directly from the tenant's
///     <c>IArchiveRuntimeStore</c>) onto the wire-shape <see cref="ArchiveSchemaDto"/> used by the
///     export <c>metadata.archive</c> block and by the import §6 schema-match. This is the bot-local
///     equivalent of the asset-repo <c>StreamDataController.MapSchema</c>: now that the bot accesses
///     CrateDB / the runtime store directly (AB#4230, direct-CrateDB rework), the same mapping that
///     used to live behind the asset-repo REST endpoint is performed in-process. Keeping the metadata
///     shape identical (<c>kind</c> derived from <see cref="ArchiveSnapshot.IsTimeRange"/> /
///     <see cref="ArchiveSnapshot.RollupAggregations"/>) means already-produced export ZIPs round-trip
///     unchanged and the field-level <see cref="ArchiveSchemaMatcher"/> stays DTO-vs-DTO.
/// </summary>
public static class ArchiveSchemaMapper
{
    /// <summary>
    ///     Projects the parts of <paramref name="snapshot"/> the export/import flow needs onto an
    ///     <see cref="ArchiveSchemaDto"/>. <c>kind</c> is <c>rollup</c> when the snapshot carries
    ///     rollup aggregations, <c>timeRange</c> when it is a time-range archive, otherwise <c>raw</c>.
    /// </summary>
    public static ArchiveSchemaDto ToDto(ArchiveSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var kind = snapshot.RollupAggregations is not null ? "rollup"
            : snapshot.IsTimeRange ? "timeRange"
            : "raw";

        var columns = snapshot.Columns
            .Select(c => new ArchiveColumnDto(c.Path, c.Indexed, c.Required))
            .ToList();

        var rollupAggregations = snapshot.RollupAggregations?
            .Select(a => new ArchiveRollupAggregationDto(a.SourcePath, a.Function.ToString(), a.TargetColumnName))
            .ToList();

        return new ArchiveSchemaDto(
            snapshot.RtId.ToString(),
            snapshot.RtWellKnownName,
            kind,
            snapshot.TargetCkTypeId.ToString(),
            columns,
            rollupAggregations,
            snapshot.Period is { } p ? (long)p.TotalMilliseconds : null);
    }
}
