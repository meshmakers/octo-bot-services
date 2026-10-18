using System.Text.Json;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.BotServices.Controllers;

/// <summary>
///     Reads the outcome of a restore from the stored job result (AB#6969): the job state stays <c>Succeeded</c> (concept
///     decision 4), but a restore whose archives failed or whose rollups could not be checked must not look like a plain
///     success to the CLI. Results of other job kinds (a file reference, a dump) are left alone.
/// </summary>
internal static class RestoreOutcome
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    ///     Sets <see cref="JobDto.Outcome" />, <see cref="JobDto.FailedArchives" /> and <see cref="JobDto.NotCheckedRollups" />
    ///     from the serialized job result when it is a restore result; does nothing otherwise.
    /// </summary>
    public static void Apply(JobDto dto, string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson) || !resultJson.TrimStart().StartsWith('{'))
        {
            return;
        }

        RestoreResultShape? result;
        try
        {
            result = JsonSerializer.Deserialize<RestoreResultShape>(resultJson, Options);
        }
        catch (JsonException)
        {
            return;
        }

        if (result is null || (result.Archives is null && result.RollupReconcile is null &&
                               result.BackupFormatVersion is null))
        {
            return;
        }

        var reconcile = result.RollupReconcile;
        var failed = (reconcile?.FailedArchives ?? [])
            .Select(f => new JobFailedArchiveDto(f.ArchiveRtId ?? string.Empty, f.Reason)).ToList();

        // The reconcile is absent when the data store has no rollup validation, yet an archive can still have failed:
        // the per-archive outcomes are the source of truth, the reconcile list only adds to them.
        var listed = failed.Select(f => f.ArchiveRtId).ToHashSet(StringComparer.Ordinal);
        failed.AddRange((result.Archives ?? [])
            .Where(a => string.Equals(a.Outcome, "Failed", StringComparison.Ordinal) &&
                        listed.Add(a.RtId ?? string.Empty))
            .Select(a => new JobFailedArchiveDto(a.RtId ?? string.Empty, a.Detail)));
        var notChecked = (reconcile?.NotCheckedRollups ?? [])
            .Select(n => new JobNotCheckedRollupDto(n.RollupRtId ?? string.Empty, n.Cause ?? string.Empty, n.Reason,
                n.IsBlocking)).ToList();

        dto.FailedArchives = failed;
        dto.NotCheckedRollups = notChecked;
        var needsAttention = failed.Count > 0 || notChecked.Any(n => n.IsBlocking) || reconcile is { Ok: false };
        dto.Outcome = needsAttention ? "SucceededWithWarnings" : "Succeeded";
    }

    private sealed class RestoreResultShape
    {
        public int? BackupFormatVersion { get; set; }

        public List<ArchiveShape>? Archives { get; set; }

        public ReconcileShape? RollupReconcile { get; set; }
    }

    private sealed class ArchiveShape
    {
        public string? RtId { get; set; }

        public string? Outcome { get; set; }

        public string? Detail { get; set; }
    }

    private sealed class ReconcileShape
    {
        public bool Ok { get; set; }

        public List<FailedShape>? FailedArchives { get; set; }

        public List<NotCheckedShape>? NotCheckedRollups { get; set; }
    }

    private sealed class FailedShape
    {
        public string? ArchiveRtId { get; set; }

        public string? Reason { get; set; }
    }

    private sealed class NotCheckedShape
    {
        public string? RollupRtId { get; set; }

        public string? Cause { get; set; }

        public string? Reason { get; set; }

        public bool IsBlocking { get; set; }
    }
}
