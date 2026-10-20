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
        JsonValueKind.Array => ConvertArray(element),
        // An object (a record column is exported as its JSON text) keeps the raw JSON text, the non-lossy fallback.
        _ => element.GetRawText(),
    };

    /// <summary>
    ///     An array column is exported as a JSON array. The driver binds a typed .NET array to a CrateDB array column on every
    ///     CrateDB version, but not the JSON text of an array: CrateDB 5.x refuses it for a text or number array and 6.4 for
    ///     every array, including an array of records (AB#7149). So the array is read as <c>string[]</c>, <c>long?[]</c>,
    ///     <c>double?[]</c> or <c>bool?[]</c> after its elements; the elements of a record array are exported as the JSON text of
    ///     each record, so they read as <c>string[]</c> too. An empty array reads as <c>string[]</c>; mixed or nested
    ///     elements keep the raw JSON text.
    /// </summary>
    private static object ConvertArray(JsonElement array)
    {
        var items = array.EnumerateArray().ToList();
        var kinds = items.Select(i => i.ValueKind).Where(k => k != JsonValueKind.Null).Distinct().ToList();

        static bool IsNull(JsonElement e) => e.ValueKind == JsonValueKind.Null;

        return kinds switch
        {
            [] or [JsonValueKind.String] => items.Select(i => IsNull(i) ? null : i.GetString()).ToArray(),
            [JsonValueKind.Object] => items.Select(i => IsNull(i) ? null : i.GetRawText()).ToArray(),
            [JsonValueKind.Number] when items.All(i => IsNull(i) || i.TryGetInt64(out _)) =>
                items.Select(i => IsNull(i) ? (long?)null : i.GetInt64()).ToArray(),
            [JsonValueKind.Number] => items.Select(i => IsNull(i) ? (double?)null : i.GetDouble()).ToArray(),
            [JsonValueKind.True] or [JsonValueKind.False] or [JsonValueKind.True, JsonValueKind.False]
                or [JsonValueKind.False, JsonValueKind.True] => items.Select(i => IsNull(i) ? (bool?)null : i.GetBoolean()).ToArray(),
            _ => array.GetRawText(),
        };
    }
}
