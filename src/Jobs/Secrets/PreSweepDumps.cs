using Meshmakers.Octo.Runtime.Contracts.Secrets;

namespace Meshmakers.Octo.Backend.Jobs.Secrets;

/// <summary>
///     Naming of pre-sweep dumps (AB#5539, AB#5559, AB#5561).
/// </summary>
public static class PreSweepDumps
{
    /// <summary>
    ///     Suffix of a pre-sweep dump in the artifact store (category <c>presweep</c>), always encrypted.
    /// </summary>
    public const string FileSuffix = ".presweep" + SecretFileFormat.FileExtension;

    /// <summary>
    ///     Suffix of a legacy pre-sweep dump on the local disk (<c>Bot:SecretSweep:BackupStoragePath</c>, before
    ///     AB#5561): plaintext mongodump. These keep being expired by the hourly cleanup; they are not migrated.
    /// </summary>
    public const string LegacyFileSuffix = ".presweep.tar.gz";

    /// <summary>
    ///     Suffix of the plaintext scratch file mongodump writes.
    /// </summary>
    internal const string ScratchSuffix = ".presweep.tar.gz";

    /// <summary>
    ///     <c>&lt;tenant&gt;-&lt;yyyyMMdd-HHmmss&gt;-&lt;guid8&gt;.presweep</c>; the store appends <c>.octoenc</c>.
    /// </summary>
    public static string NewBaseFileName(string tenantId, DateTime utcNow)
    {
        return $"{tenantId.ToLowerInvariant()}-{utcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.presweep";
    }

    /// <summary>
    ///     True for a dump kept in the artifact store.
    /// </summary>
    public static bool IsStoredDump(string? fileName)
    {
        return fileName != null && fileName.EndsWith(FileSuffix, StringComparison.Ordinal);
    }

    /// <summary>
    ///     True for a legacy local dump.
    /// </summary>
    public static bool IsLegacyDump(string? fileName)
    {
        return fileName != null && fileName.EndsWith(LegacyFileSuffix, StringComparison.Ordinal);
    }
}
