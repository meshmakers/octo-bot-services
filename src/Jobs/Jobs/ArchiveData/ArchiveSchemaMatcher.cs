using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;

/// <summary>
///     Pure, side-effect-free schema-match validation between an exported archive schema (from the
///     import ZIP's <c>metadata.json</c>) and the live target archive schema. Implements the §6
///     "hard requirement": <c>targetCkTypeId</c>, column set (order-independent, keyed by
///     <c>Path</c>, matching <c>Indexed</c>/<c>Required</c>), <c>kind</c>, and — for rollups — the
///     rollup aggregation specs must all match exactly.
///     <para>
///     On any mismatch a <b>specific, field-level</b> message is produced (naming the offending
///     column/field), per the <c>feedback_rtid_must_be_hex</c> precedent of surfacing per-field
///     detail rather than a generic "invalid model". The frontend surfaces the message verbatim.
///     </para>
///     Kept as a static helper so it is unit-testable in isolation from the Hangfire/SDK plumbing.
/// </summary>
public static class ArchiveSchemaMatcher
{
    /// <summary>
    ///     Validates that <paramref name="source"/> (the exported schema) matches
    ///     <paramref name="target"/> (the live target archive schema). Returns <c>null</c> when the
    ///     schemas match; otherwise returns a specific, field-level rejection message.
    /// </summary>
    /// <param name="source">Schema taken from the export ZIP's <c>metadata.archive</c> block.</param>
    /// <param name="target">Live schema of the import target archive.</param>
    /// <returns><c>null</c> on a match; a field-level error message on any mismatch.</returns>
    public static string? FindMismatch(ArchiveSchemaDto source, ArchiveSchemaDto target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var targetName = string.IsNullOrEmpty(target.RtWellKnownName) ? target.RtId : target.RtWellKnownName;

        // §6.3 — kind must agree (windowed vs point storage layout must match).
        if (!string.Equals(source.Kind, target.Kind, StringComparison.OrdinalIgnoreCase))
        {
            return
                $"Import rejected: target archive '{targetName}' is of kind '{target.Kind}' but the export was taken " +
                $"from a '{source.Kind}' archive. Schemas must match exactly.";
        }

        // §6.1 — same archived CK type.
        if (!string.Equals(source.TargetCkTypeId, target.TargetCkTypeId, StringComparison.Ordinal))
        {
            return
                $"Import rejected: target archive '{targetName}' archives CK type '{target.TargetCkTypeId}' but the " +
                $"export was taken from CK type '{source.TargetCkTypeId}'. Schemas must match exactly.";
        }

        // §6.2 — column set equal as a set keyed by Path, with matching Indexed + Required.
        var columnMismatch = FindColumnMismatch(source.Columns, target.Columns, targetName);
        if (columnMismatch != null)
        {
            return columnMismatch;
        }

        // §6.4 — for rollups, the aggregation specs must match.
        if (string.Equals(target.Kind, "rollup", StringComparison.OrdinalIgnoreCase))
        {
            var rollupMismatch = FindRollupMismatch(source.RollupAggregations, target.RollupAggregations, targetName);
            if (rollupMismatch != null)
            {
                return rollupMismatch;
            }
        }

        return null;
    }

    private static string? FindColumnMismatch(
        IReadOnlyList<ArchiveColumnDto>? sourceColumns,
        IReadOnlyList<ArchiveColumnDto>? targetColumns,
        string targetName)
    {
        var source = sourceColumns ?? Array.Empty<ArchiveColumnDto>();
        var target = targetColumns ?? Array.Empty<ArchiveColumnDto>();

        var sourceByPath = new Dictionary<string, ArchiveColumnDto>(StringComparer.Ordinal);
        foreach (var column in source)
        {
            // Duplicate paths within one schema are themselves an error worth surfacing.
            if (!sourceByPath.TryAdd(column.Path, column))
            {
                return
                    $"Import rejected: the export schema declares column '{column.Path}' more than once. " +
                    "Schemas must match exactly.";
            }
        }

        var targetByPath = new Dictionary<string, ArchiveColumnDto>(StringComparer.Ordinal);
        foreach (var column in target)
        {
            targetByPath[column.Path] = column;
        }

        // Column present in the export but not on the target.
        foreach (var column in source)
        {
            if (!targetByPath.ContainsKey(column.Path))
            {
                return
                    $"Import rejected: target archive '{targetName}' has no column '{column.Path}' but the export " +
                    "declares it. Schemas must match exactly.";
            }
        }

        // Column required by the target but missing from the export.
        foreach (var column in target)
        {
            if (!sourceByPath.ContainsKey(column.Path))
            {
                return
                    $"Import rejected: target archive '{targetName}' expects column '{column.Path}' " +
                    $"(indexed={Lower(column.Indexed)}, required={Lower(column.Required)}) but the export was taken " +
                    "from an archive without it. Schemas must match exactly.";
            }
        }

        // Same set of paths — now compare the per-column flags.
        foreach (var column in source)
        {
            var targetColumn = targetByPath[column.Path];

            if (column.Indexed != targetColumn.Indexed)
            {
                return
                    $"Import rejected: column '{column.Path}' on target archive '{targetName}' is " +
                    $"indexed={Lower(targetColumn.Indexed)} but the export has indexed={Lower(column.Indexed)}. " +
                    "Schemas must match exactly.";
            }

            if (column.Required != targetColumn.Required)
            {
                return
                    $"Import rejected: column '{column.Path}' on target archive '{targetName}' is " +
                    $"required={Lower(targetColumn.Required)} but the export has required={Lower(column.Required)}. " +
                    "Schemas must match exactly.";
            }
        }

        return null;
    }

    private static string? FindRollupMismatch(
        IReadOnlyList<ArchiveRollupAggregationDto>? sourceAggregations,
        IReadOnlyList<ArchiveRollupAggregationDto>? targetAggregations,
        string targetName)
    {
        var source = sourceAggregations ?? Array.Empty<ArchiveRollupAggregationDto>();
        var target = targetAggregations ?? Array.Empty<ArchiveRollupAggregationDto>();

        if (source.Count != target.Count)
        {
            return
                $"Import rejected: target rollup archive '{targetName}' defines {target.Count} aggregation(s) but the " +
                $"export defines {source.Count}. Schemas must match exactly.";
        }

        // Key by source path; order-independent.
        var targetByKey = new Dictionary<string, ArchiveRollupAggregationDto>(StringComparer.Ordinal);
        foreach (var aggregation in target)
        {
            targetByKey[RollupKey(aggregation)] = aggregation;
        }

        foreach (var aggregation in source)
        {
            if (!targetByKey.TryGetValue(RollupKey(aggregation), out var targetAggregation))
            {
                return
                    $"Import rejected: target rollup archive '{targetName}' has no aggregation '{aggregation.Function}' " +
                    $"on source path '{aggregation.SourcePath}' but the export declares it. Schemas must match exactly.";
            }

            if (!string.Equals(aggregation.TargetColumnName, targetAggregation.TargetColumnName, StringComparison.Ordinal))
            {
                return
                    $"Import rejected: aggregation '{aggregation.Function}' on source path '{aggregation.SourcePath}' " +
                    $"targets column '{Display(aggregation.TargetColumnName)}' in the export but " +
                    $"'{Display(targetAggregation.TargetColumnName)}' on target rollup archive '{targetName}'. " +
                    "Schemas must match exactly.";
            }
        }

        return null;
    }

    private static string RollupKey(ArchiveRollupAggregationDto aggregation)
    {
        return $"{aggregation.SourcePath} {aggregation.Function}";
    }

    private static string Lower(bool value)
    {
        return value ? "true" : "false";
    }

    private static string Display(string? value)
    {
        return value ?? "(engine-derived)";
    }
}
