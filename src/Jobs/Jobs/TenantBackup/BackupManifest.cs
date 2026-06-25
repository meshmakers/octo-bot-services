using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.Jobs.Jobs.TenantBackup;

/// <summary>
///     The <c>manifest.json</c> entry written into (and read back from) an <c>.octobak.zip</c> tenant
///     backup container. Present only when the backup was produced with <c>includeArchiveData = true</c>;
///     a ZIP carrying it is the discriminator that marks the artifact as an <c>.octobak</c> (anything
///     else is a legacy mongodump <c>.tar.gz</c>). Tenant backup archive-data concept (AB#4231) §3.1.
/// </summary>
/// <param name="FormatVersion">File-format version gate. Current format is <c>1</c>.</param>
/// <param name="CreatedAtUtc">When the backup was produced (UTC).</param>
/// <param name="SourceTenantId">Provenance: the tenant the backup was taken from.</param>
/// <param name="IncludesArchiveData">Always <c>true</c> for an <c>.octobak</c>; carried for forward clarity.</param>
/// <param name="Archives">One entry per tenant archive captured by the backup.</param>
public sealed record BackupManifest(
    int FormatVersion,
    DateTime CreatedAtUtc,
    string SourceTenantId,
    bool IncludesArchiveData,
    IReadOnlyList<BackupManifestArchive> Archives);

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
/// <param name="NdjsonEntry">
///     Relative ZIP path of this archive's row data (<c>archives/&lt;rtId&gt;.ndjson</c>), or <c>null</c>
///     when the archive had no provisioned Crate table at backup time (status <c>Created</c>/<c>Failed</c>)
///     and therefore carries no data to restore.
/// </param>
public sealed record BackupManifestArchive(
    ArchiveSchemaDto Schema,
    string Status,
    long RowCount,
    string? NdjsonEntry);

/// <summary>
///     Stable entry names + helpers for the <c>.octobak.zip</c> tenant backup container layout
///     (concept §3): the verbatim mongodump blob, the manifest, and the per-archive NDJSON folder.
/// </summary>
public static class BackupArchiveContainer
{
    /// <summary>Current backup manifest format version.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>The verbatim <c>mongodump --archive --gzip</c> blob entry.</summary>
    public const string MongoBlobEntry = "mongo.tar.gz";

    /// <summary>The backup manifest entry (its presence marks the ZIP as an <c>.octobak</c>).</summary>
    public const string ManifestEntry = "manifest.json";

    /// <summary>The folder prefix holding one <c>&lt;rtId&gt;.ndjson</c> per archive.</summary>
    public const string ArchivesFolder = "archives/";

    /// <summary>Builds the relative ZIP entry path of an archive's row data.</summary>
    public static string NdjsonEntryFor(string rtId) => $"{ArchivesFolder}{rtId}.ndjson";
}
