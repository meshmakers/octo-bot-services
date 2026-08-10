using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;

/// <summary>
///     Streams an archive NDJSON entry into row dictionaries carrying CLR values. Deserialising a
///     line into <c>Dictionary&lt;string, object?&gt;</c> leaves every value as a
///     <see cref="JsonElement"/>, which the engine's <c>ImportRowsAsync</c> cannot consume — its
///     <c>as string</c> / <c>DateTime</c> pattern matches all miss, so every non-empty archive failed
///     with "field 'rtid' must be a 24-character hex string, but was '(null)'". Each value is
///     therefore unwrapped here, before it crosses the <c>IStreamDataRepository</c> contract.
/// </summary>
internal static class NdjsonRowReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     Physical time-axis column names of the export row format (raw and windowed storage
    ///     shapes). Only these are parsed into <see cref="DateTime"/>; user columns keep their JSON
    ///     string form (CrateDB coerces ISO strings into timestamp columns on insert).
    /// </summary>
    private static readonly HashSet<string> TimestampColumns = new(StringComparer.Ordinal)
    {
        "timestamp", "window_start", "window_end", "rtcreationdatetime", "rtchangeddatetime",
    };

    /// <summary>
    ///     Reads NDJSON one line at a time, deserialising each non-blank line into a row dictionary.
    ///     Streamed (never fully buffered) so multi-GB imports stay flat in memory. <paramref name="onRow"/>
    ///     is invoked once per yielded row (row counting for the restore summary).
    /// </summary>
    public static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadRowsAsync(
        Stream body, Action? onRow, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 64 * 1024, leaveOpen: true);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line, JsonOptions);
            if (raw is null)
            {
                continue;
            }

            var row = new Dictionary<string, object?>(raw.Count, StringComparer.Ordinal);
            foreach (var (column, element) in raw)
            {
                row[column] = ConvertValue(column, element);
            }

            onRow?.Invoke();
            yield return row;
        }
    }

    private static object? ConvertValue(string column, JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String when TimestampColumns.Contains(column) &&
                                  element.TryGetDateTimeOffset(out var dto) => dto.UtcDateTime,
        JsonValueKind.String => element.GetString(),
        // Box each branch separately — the ternary would otherwise unify long|double to double
        // and silently turn integer counts into floating-point values.
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : (object)element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        // Objects/arrays do not occur in exported rows; raw JSON text is the non-lossy fallback.
        _ => element.GetRawText(),
    };
}
