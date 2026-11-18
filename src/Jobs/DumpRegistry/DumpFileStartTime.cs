using System.Globalization;
using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Backend.Jobs.DumpRegistry;

/// <summary>
///     The earliest time a stored tenant dump can have started, read from its file name. The dump job names a
///     <c>.tar.gz</c> before mongodump starts and an <c>.octobak.zip</c> after it, which is bounded by the mongodump timeout.
///     A name that does not follow the pattern gives no time, and the dump is not registered.
/// </summary>
public static partial class DumpFileStartTime
{
    /// <summary>The longest a mongodump runs before the archive container is named (the dump job's timeout).</summary>
    public static readonly TimeSpan LongestMongoDump = TimeSpan.FromHours(1);

    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>Reads the earliest start time of the dump stored as <paramref name="storedFileName" />.</summary>
    public static bool TryRead(string storedFileName, out DateTime earliestStartUtc)
    {
        earliestStartUtc = default;
        var match = DumpFileNamePattern().Match(storedFileName);
        if (!match.Success)
        {
            return false;
        }

        var timestampText = match.Groups["timestamp"].Value;
        if (!DateTime.TryParseExact(timestampText, TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var namedAtUtc))
        {
            return false;
        }

        var isNamedAfterMongoDump = match.Groups["extension"].Value == ".octobak.zip";
        earliestStartUtc = isNamedAfterMongoDump ? namedAtUtc - LongestMongoDump : namedAtUtc;
        return true;
    }

    [GeneratedRegex(@"-(?<timestamp>\d{8}-\d{6})-[0-9a-f]{8}(?<extension>\.tar\.gz|\.octobak\.zip)(\.octoenc)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DumpFileNamePattern();
}
