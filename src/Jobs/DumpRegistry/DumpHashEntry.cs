namespace Meshmakers.Octo.Backend.Jobs.DumpRegistry;

/// <summary>A dump the platform produced for a tenant.</summary>
/// <param name="TenantId">The tenant the dump was taken of, lower case.</param>
/// <param name="Sha256">SHA-256 of the file as the download serves it, lower-case hex.</param>
/// <param name="DumpedAtUtc">When the dump started (the backfill: the earliest time it can have started).</param>
/// <param name="ExpiresAtUtc">When the entry stops admitting a restore.</param>
/// <param name="FileName">The stored file name of the dump.</param>
/// <param name="SizeBytes">Size of the file as the download serves it.</param>
/// <param name="JobId">The dump job of <c>Dump-1</c>; <c>null</c> for the REST dump and for an entry of the backfill.</param>
/// <param name="Source">Who wrote the entry: <see cref="DumpHashSources.Dump" /> or <see cref="DumpHashSources.Backfill" />.</param>
public sealed record DumpHashEntry(
    string TenantId,
    string Sha256,
    DateTime DumpedAtUtc,
    DateTime ExpiresAtUtc,
    string FileName,
    long SizeBytes,
    string? JobId,
    string Source);

/// <summary>The writers of a <see cref="DumpHashEntry" />.</summary>
public static class DumpHashSources
{
    /// <summary>The dump job recorded its own dump.</summary>
    public const string Dump = "dump";

    /// <summary>The backfill recorded a dump that was stored before the registry existed.</summary>
    public const string Backfill = "backfill";
}
