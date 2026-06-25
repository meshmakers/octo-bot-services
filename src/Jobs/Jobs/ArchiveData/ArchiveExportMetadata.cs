using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;

/// <summary>
///     The exported window of an archive (<c>[FromUtc, ToUtc)</c>). <c>null</c> on the parent metadata
///     means the whole archive was exported. Archive data export/import concept (AB#4230) §3.1.
/// </summary>
/// <param name="FromUtc">Inclusive lower bound (UTC).</param>
/// <param name="ToUtc">Exclusive upper bound (UTC).</param>
public sealed record ArchiveExportWindow(DateTime FromUtc, DateTime ToUtc);

/// <summary>
///     The <c>metadata.json</c> entry written into (and read back from) the export ZIP. Mirrors the
///     concept §3.1 file format exactly so an import can validate the schema match before any write.
/// </summary>
/// <param name="FormatVersion">File-format version gate. Current format is <c>1</c>.</param>
/// <param name="ExportedAtUtc">When the export was produced (UTC).</param>
/// <param name="SourceTenantId">Provenance: the tenant the data was exported from.</param>
/// <param name="Archive">The archive schema (== <c>metadata.archive</c> block); the import match key.</param>
/// <param name="Window">The exported slice, or <c>null</c> when the whole archive was exported.</param>
/// <param name="RowCount">Advisory row count for progress / post-import sanity. <c>null</c> when not computed.</param>
public sealed record ArchiveExportMetadata(
    int FormatVersion,
    DateTime ExportedAtUtc,
    string SourceTenantId,
    ArchiveSchemaDto Archive,
    ArchiveExportWindow? Window,
    long? RowCount);
