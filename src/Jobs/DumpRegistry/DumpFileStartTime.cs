using System.Globalization;
using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Backend.Jobs.DumpRegistry;

/// <summary>
///     The earliest time a stored tenant dump can have started, read from its file name. Only a MongoDB dump
///     (<c>.tar.gz</c>) carries it: the dump job names it before mongodump starts. An archive container
///     (<c>.octobak.zip</c>) is named after mongodump and the archive copy, which has no time limit, so its name gives no
///     safe start time. A name without a safe start time gives none, and the dump is not registered.
/// </summary>
public static partial class DumpFileStartTime
{
    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>Reads the earliest start time of the dump stored as <paramref name="storedFileName" />.</summary>
    public static bool TryRead(string storedFileName, out DateTime earliestStartUtc)
    {
        earliestStartUtc = default;
        var match = MongoDumpFileNamePattern().Match(storedFileName);
        if (!match.Success)
        {
            return false;
        }

        var timestampText = match.Groups["timestamp"].Value;
        return DateTime.TryParseExact(timestampText, TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out earliestStartUtc);
    }

    [GeneratedRegex(@"-(?<timestamp>\d{8}-\d{6})-[0-9a-f]{8}\.tar\.gz(\.octoenc)?$", RegexOptions.CultureInvariant)]
    private static partial Regex MongoDumpFileNamePattern();
}
