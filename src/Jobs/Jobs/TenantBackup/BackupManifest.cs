using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.Jobs.Jobs.TenantBackup;

/// <summary>
///     The <c>manifest.json</c> entry written into (and read back from) an <c>.octobak.zip</c> tenant
///     backup container. Present only when the backup was produced with <c>includeArchiveData = true</c>;
///     a ZIP carrying it is the discriminator that marks the artifact as an <c>.octobak</c> (anything
///     else is a legacy mongodump <c>.tar.gz</c>). Tenant backup archive-data concept (AB#4231) §3.1.
/// </summary>
/// <param name="FormatVersion">
///     File-format version gate: <c>1</c> (rows as NDJSON entries) or <c>2</c> (the files of the native archive copy,
///     AB#5967, see <see cref="BackupArchiveContainer.NativeFormatVersion"/>).
/// </param>
/// <param name="CreatedAtUtc">When the backup was produced (UTC).</param>
/// <param name="SourceTenantId">Provenance: the tenant the backup was taken from.</param>
/// <param name="IncludesArchiveData">Always <c>true</c> for an <c>.octobak</c>; carried for forward clarity.</param>
/// <param name="Archives">One entry per tenant archive captured by the backup.</param>
/// <param name="Native">Format 2 only: where the backup came from (storage version, platform version, moments of the dump).</param>
public sealed record BackupManifest(
    int FormatVersion,
    DateTime CreatedAtUtc,
    string SourceTenantId,
    bool IncludesArchiveData,
    IReadOnlyList<BackupManifestArchive> Archives,
    BackupManifestNative? Native = null);

/// <summary>
///     Format 2 (AB#5967): provenance of a native archive dump. The restore logs the storage version next to the
///     target's; it does not refuse a difference (a backup from CrateDB 5.x restores on 6.x).
/// </summary>
/// <param name="StorageVersion">Version of the data store the archives were copied from (CrateDB <c>sys.nodes</c>).</param>
/// <param name="PlatformVersion">Version of the bot service that wrote the backup.</param>
/// <param name="MongoDumpFinishedAtUtc">When mongodump finished — before any archive was copied (concept §6).</param>
public sealed record BackupManifestNative(string? StorageVersion, string PlatformVersion, DateTime MongoDumpFinishedAtUtc);

/// <summary>
///     One archive's entry in the backup <see cref="BackupManifest"/>. Combines the AB#4230
///     <see cref="ArchiveSchemaDto"/> projection (produced by <c>ArchiveSchemaMapper.ToDto</c>, the
///     import §6 schema-match key) with the archive's backed-up lifecycle <see cref="Status"/>, the
///     advisory <see cref="RowCount"/>, and the relative <see cref="NdjsonEntry"/> path of its row
///     data inside the ZIP. Tenant backup archive-data concept (AB#4231) §3.1.
/// </summary>
/// <param name="Schema">The archive schema (== AB#4230 <c>metadata.archive</c> block); the restore §6 match key.</param>
/// <param name="Status">
///     The archive's lifecycle status at backup time (<c>CkArchiveStatus</c> name). The restore job
///     restores each archive to this status after the clean re-import (concept §5.1 / §10).
/// </param>
/// <param name="RowCount">Advisory exported row count (<c>0</c> for an archive with no provisioned table).</param>
/// <param name="Copy">
///     Format 2: the native copy of this archive (tables, slices, the moments of the generation map read and of the last
///     data slice); <c>null</c> in format 1 and for an archive without a table.
/// </param>
/// <param name="NdjsonEntry">
///     Relative ZIP path of this archive's row data (<c>archives/&lt;rtId&gt;.ndjson</c>), or <c>null</c>
///     when the archive had no provisioned Crate table at backup time and therefore carries no data to
///     restore. The table, not the status, decides (AB#5141): a blueprint-seeded archive is
///     <c>Disabled</c> without a table until its first activation, and the restore leaves such an
///     archive exactly as backed up (status kept, no table created).
/// </param>
public sealed record BackupManifestArchive(
    ArchiveSchemaDto Schema,
    string Status,
    long RowCount,
    string? NdjsonEntry,
    BackupManifestArchiveCopy? Copy = null);

/// <summary>
///     Format 2: what the dump copied of one archive. The restore rebuilds the engine's copy plan from it.
/// </summary>
/// <param name="Tables">The tables with their columns and backend data types at dump time.</param>
/// <param name="Slices">The slices in copy order (data slices ascending in time, the generation map last).</param>
/// <param name="Attempts">How often the rollup was copied (1 unless its generation map moved meanwhile).</param>
/// <param name="MapReadAtUtc">When the generation map was read for the attempt that stands; <c>null</c> without a map.</param>
/// <param name="LastDataCopiedAtUtc">When the last data slice of that attempt was copied.</param>
public sealed record BackupManifestArchiveCopy(
    IReadOnlyList<BackupManifestTable> Tables,
    IReadOnlyList<BackupManifestSlice> Slices,
    int Attempts,
    DateTime? MapReadAtUtc,
    DateTime? LastDataCopiedAtUtc);

/// <summary>One table of an archive: <c>Data</c> or <c>GenerationMap</c>.</summary>
public sealed record BackupManifestTable(string Table, IReadOnlyList<BackupManifestColumn> Columns);

/// <summary>A column of a dumped table with its backend data type.</summary>
public sealed record BackupManifestColumn(string Name, string DataType);

/// <summary>
///     One slice of a native copy: a folder <c>&lt;rtId&gt;/data/&lt;key&gt;</c> (or <c>genmap/all</c>) of the container.
/// </summary>
/// <param name="Table"><c>Data</c> or <c>GenerationMap</c>.</param>
/// <param name="Key">The folder name of the slice.</param>
/// <param name="FromUtc">Inclusive start of the time range; <c>null</c> for the generation map.</param>
/// <param name="ToUtc">Exclusive end of the time range; <c>null</c> for the generation map.</param>
/// <param name="RowCount">The rows a restore has to load back (what the copy reported).</param>
/// <param name="CountedRows">The rows counted before the copy.</param>
/// <param name="CopiedRows">The rows the <c>COPY TO</c> statement reported.</param>
/// <param name="StagedBytes">Size of the slice's files; a restore uses it for the free disk space check.</param>
/// <param name="Message">A warning on the slice (counted and copied rows differ); already redacted.</param>
public sealed record BackupManifestSlice(
    string Table,
    string Key,
    DateTime? FromUtc,
    DateTime? ToUtc,
    long RowCount,
    long CountedRows,
    long CopiedRows,
    long? StagedBytes,
    string? Message);

/// <summary>
///     Stable entry names + helpers for the <c>.octobak.zip</c> tenant backup container layout
///     (concept §3): the verbatim mongodump blob, the manifest, and the per-archive NDJSON folder.
/// </summary>
public static class BackupArchiveContainer
{
    /// <summary>Format version of a backup whose archives are NDJSON entries (rows through the service).</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>
    ///     Format version of a backup whose archives are the files of the native copy
    ///     (<c>archives/&lt;rtId&gt;/data/&lt;slice&gt;/…json.gz</c>, <c>archives/&lt;rtId&gt;/genmap/all/…</c>).
    /// </summary>
    public const int NativeFormatVersion = 2;

    /// <summary>The format versions this service reads: 1 and 2.</summary>
    public const string SupportedFormatVersionsText = "1 and 2";

    /// <summary>True for the format versions this service reads.</summary>
    public static bool IsSupported(int formatVersion) => formatVersion is CurrentFormatVersion or NativeFormatVersion;

    /// <summary>The verbatim <c>mongodump --archive --gzip</c> blob entry.</summary>
    public const string MongoBlobEntry = "mongo.tar.gz";

    /// <summary>The backup manifest entry (its presence marks the ZIP as an <c>.octobak</c>).</summary>
    public const string ManifestEntry = "manifest.json";

    /// <summary>The folder prefix holding one <c>&lt;rtId&gt;.ndjson</c> per archive.</summary>
    public const string ArchivesFolder = "archives/";

    /// <summary>Builds the relative ZIP entry path of an archive's row data.</summary>
    public static string NdjsonEntryFor(string rtId) => $"{ArchivesFolder}{rtId}.ndjson";

    /// <summary>Builds the ZIP entry path of a file of the native copy from its path below the run folder.</summary>
    public static string NativeEntryFor(string relativePath) => ArchivesFolder + relativePath;

    /// <summary>
    ///     True for an entry of the native copy (<c>archives/&lt;rtId&gt;/…</c>, a folder level below
    ///     <see cref="ArchivesFolder"/>); the row entries of format 1 (<c>archives/&lt;rtId&gt;.ndjson</c>) are not.
    /// </summary>
    public static bool TryGetNativeRelativePath(string entryName, out string relativePath)
    {
        relativePath = string.Empty;
        if (!entryName.StartsWith(ArchivesFolder, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = entryName[ArchivesFolder.Length..];
        if (rest.Length == 0 || !rest.Contains('/') || rest.EndsWith('/') || rest.EndsWith(".ndjson", StringComparison.Ordinal))
        {
            return false;
        }

        relativePath = rest;
        return true;
    }
}
