using System.Text.Json;

namespace Meshmakers.Octo.Backend.Jobs.Jobs.Operations;

/// <summary>Stage codes of a restore started by <c>System.Bot/TenantRepository.Restore-1</c> (AB#8447).</summary>
public static class RestoreStages
{
    /// <summary>The job is enqueued and waits for a worker.</summary>
    public const string Queued = DumpStages.Queued;

    /// <summary>The uploaded backup is read from the artifact store (assembled from its tus parts when needed).</summary>
    public const string PreparingUpload = "PREPARING_UPLOAD";

    /// <summary>mongorestore replaces the tenant database.</summary>
    public const string RestoringDatabase = "RESTORING_DATABASE";

    /// <summary>The archives of the backup are restored; parameters <c>archive</c> and <c>archiveCount</c>.</summary>
    public const string RestoringArchives = "RESTORING_ARCHIVES";

    /// <summary>The post-restore secret sweep runs (AB#5539).</summary>
    public const string SweepingSecrets = "SWEEPING_SECRETS";
}

/// <summary>Stage codes of the archive data export and import methods (AB#8447).</summary>
public static class ArchiveDataStages
{
    /// <summary>The job is enqueued and waits for a worker.</summary>
    public const string Queued = DumpStages.Queued;

    /// <summary>The upload is read from the artifact store and the schema of the target archive is checked.</summary>
    public const string Validating = "VALIDATING";

    /// <summary>Rows are written to the export file; parameter <c>rows</c> (rows so far).</summary>
    public const string ExportingRows = "EXPORTING_ROWS";

    /// <summary>Rows are imported into the archive; parameter <c>rows</c> (rows read so far).</summary>
    public const string ImportingRows = "IMPORTING_ROWS";

    /// <summary>The imported window of a rollup archive is frozen.</summary>
    public const string FreezingRollupWindow = "FREEZING_ROLLUP_WINDOW";

    /// <summary>
    ///     Progress of a row pump: no percentage (the total is not known before the end), the stage and the rows so far.
    /// </summary>
    public static DumpProgress Rows(string stageCode, long rows) =>
        new(-1, stageCode, new Dictionary<string, string>
        {
            ["rows"] = rows.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });

    /// <summary>Rows between two progress reports of a row pump.</summary>
    public const int ReportEveryRows = 10_000;
}

/// <summary>
///     The result summary a restore started by a method writes as the job parameter
///     <see cref="OperationJobParameters.Result" /> (AB#8447); the handler turns it into
///     <c>System.Bot/RepositoryRestoreResult</c>. Counts only, never an archive id, a secret or a value.
/// </summary>
/// <param name="ArchiveCount">Archives of the backup that were handled (restored, skipped or failed).</param>
/// <param name="FailedArchiveCount">Archives that could not be restored.</param>
/// <param name="SecretsToReEnterCount">Secrets the post-restore sweep could not read.</param>
public sealed record RestoreOperationSummary(int ArchiveCount, int FailedArchiveCount, int SecretsToReEnterCount)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The summary of a restore result.</summary>
    public static RestoreOperationSummary From(RestoreRepositoryResult result) =>
        new(result.Archives.Count,
            result.Archives.Count(a => string.Equals(a.Outcome, "Failed", StringComparison.Ordinal)),
            result.SecretSweep?.SecretsToReEnter.Count ?? 0);

    /// <summary>Serializes the summary for the job parameter.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Reads a summary written by <see cref="ToJson" />; <c>null</c> for an empty or unreadable value.</summary>
    public static RestoreOperationSummary? FromJson(string? json) => OperationSummaryJson.Read<RestoreOperationSummary>(json);
}

/// <summary>
///     The result summary of an archive data export or import started by a method (AB#8447): the rows written to the
///     export file or read from the import file.
/// </summary>
/// <param name="RowCount">Rows written (export) or read (import).</param>
public sealed record ArchiveDataOperationSummary(long RowCount)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Serializes the summary for the job parameter.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Reads a summary written by <see cref="ToJson" />; <c>null</c> for an empty or unreadable value.</summary>
    public static ArchiveDataOperationSummary? FromJson(string? json) =>
        OperationSummaryJson.Read<ArchiveDataOperationSummary>(json);
}

internal static class OperationSummaryJson
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
