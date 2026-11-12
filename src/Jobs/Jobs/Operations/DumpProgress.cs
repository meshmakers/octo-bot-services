using System.Text.Json;

namespace Meshmakers.Octo.Backend.Jobs.Jobs.Operations;

/// <summary>
///     One progress report of a repository dump (AB#8445): a percentage, a language-neutral stage code and string
///     parameters. The dump job writes it as the Hangfire job parameter <see cref="DumpJobParameters.Progress" />; the
///     handler of the async method <c>System.Bot/TenantRepository.Dump-1</c> relays it into the operation record.
///     Since AB#8447 the restore and archive data jobs report with the same record (job parameter
///     <see cref="OperationJobParameters.Progress" />); a negative <paramref name="Percent" /> means "not known" (row pumps).
/// </summary>
/// <param name="Percent">Completion percentage, 0 to 100.</param>
/// <param name="StageCode">Stage code (UPPER_SNAKE_CASE), see <see cref="DumpStages" />.</param>
/// <param name="Parameters">Parameters of the stage (e.g. the archive number), never a value of the tenant's data.</param>
public sealed record DumpProgress(int Percent, string StageCode, IReadOnlyDictionary<string, string>? Parameters = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Serializes the report for the job parameter.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Reads a report written by <see cref="ToJson" />; <c>null</c> for an empty or unreadable value.</summary>
    public static DumpProgress? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DumpProgress>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Stage codes of a repository dump (AB#8445).</summary>
public static class DumpStages
{
    /// <summary>The job is enqueued and waits for a worker.</summary>
    public const string Queued = "QUEUED";

    /// <summary>mongodump of the tenant database runs.</summary>
    public const string DumpingDatabase = "DUMPING_DATABASE";

    /// <summary>The archive (stream data) rows are exported; parameters <c>archive</c> and <c>archiveCount</c>.</summary>
    public const string ExportingArchives = "EXPORTING_ARCHIVES";

    /// <summary>The finished file is stored in the artifact store.</summary>
    public const string StoringArtifact = "STORING_ARTIFACT";
}

/// <summary>Names of the Hangfire job parameters a dump started by the async method writes (AB#8445).</summary>
public static class DumpJobParameters
{
    /// <summary>The operation id of the method call that started the job.</summary>
    public const string OperationId = "OctoMethodOperationId";

    /// <summary>The latest <see cref="DumpProgress" /> as JSON.</summary>
    public const string Progress = "OctoDumpProgress";

    /// <summary>Set when the job started once; a second start of the same job (a requeue after a restart) is refused.</summary>
    public const string Started = "OctoDumpStarted";

    /// <summary>Why the job refused to run: <see cref="InterruptedOutcome" /> or <see cref="OperationClosedOutcome" />.</summary>
    public const string Outcome = "OctoDumpOutcome";

    /// <summary>The job had started before and was interrupted (the bot service stopped while it ran).</summary>
    public const string InterruptedOutcome = "interrupted";

    /// <summary>The operation of the job was no longer open when the job started (it had been ended as lost).</summary>
    public const string OperationClosedOutcome = "operation-closed";
}

/// <summary>Receives the progress of a running dump.</summary>
public interface IDumpProgressSink
{
    /// <summary>Records a progress report; never throws.</summary>
    void Report(DumpProgress progress);
}

/// <summary>Ignores every report (the classic REST dump has no progress).</summary>
public sealed class NullDumpProgressSink : IDumpProgressSink
{
    /// <summary>The single instance.</summary>
    public static NullDumpProgressSink Instance { get; } = new();

    private NullDumpProgressSink()
    {
    }

    /// <inheritdoc />
    public void Report(DumpProgress progress)
    {
    }
}

/// <summary>
///     Decides whether a dump started by an async method call may still run: the operation must be open (accepted or
///     running). Implemented by the bot service on the operation store of the method runtime (AB#8443).
/// </summary>
public interface IDumpOperationGate
{
    /// <summary><c>true</c> when the operation <paramref name="operationId" /> of the tenant is still open.</summary>
    Task<bool> IsOperationOpenAsync(string tenantId, string operationId, CancellationToken cancellationToken);
}
