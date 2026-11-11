# Changelog

## 3.8.0 - 2026-11-11

_Required bump level: minor._

### Added

- `TenantRepository` gets three static methods (`execution.mode: async`, `handler: service`, roles `TenantManagement`;
  AB#8447, Feature AB#5694 F3.6), each returning an operation handle:
  - `Restore-1` (`tusFileId`, `databaseName`, optional `oldDatabaseName`, optional `restoreArchiveData`), result record
    `RepositoryRestoreResult`, declared errors `UPLOAD_NOT_FOUND`, `RESTORE_FAILED`, `RESTORE_INTERRUPTED`;
  - `ExportArchiveData-1` (`archiveRtId`, optional `fromUtc` / `toUtc`), result record `ArchiveDataExportResult`, declared
    errors `ARCHIVE_EXPORT_FAILED`, `ARCHIVE_EXPORT_INTERRUPTED`;
  - `ImportArchiveData-1` (`tusFileId`, `archiveRtId`, optional `mode`), result record `ArchiveDataImportResult`, declared
    errors `UPLOAD_NOT_FOUND`, `ARCHIVE_IMPORT_FAILED`, `ARCHIVE_IMPORT_INTERRUPTED`.
- Records `RepositoryRestoreResult`, `ArchiveDataExportResult`, `ArchiveDataImportResult`; attributes
  `RepositoryRestore.ArchiveCount`, `RepositoryRestore.FailedArchiveCount`, `RepositoryRestore.SecretsToReEnterCount`,
  `ArchiveData.RowCount`; enum `ArchiveImportMode` (`InsertOnly` = 0, `Upsert` = 1).

### Compatibility

- Additions only: no element removed or changed, `ckLanguage` stays 2, dependency `System-[2.5,3.0)` unchanged,
  `minEngineVersion` unchanged (3.5.67, `execution.mode` was introduced by 3.7.0). Dependents (`System.Communication` 4.x,
  `System.Ai` 4.x, `Loxone` 5.x) resolve as before (range retention).

## 3.7.0 - 2026-11-10

_Required bump level: minor._

### Added

- Type `TenantRepository` (derived from `System/Entity`, no attributes, `derivable: Model`) with the static method
  `Dump-1` (`execution.mode: async`, `handler: service`, roles `TenantManagement`): starts a repository dump of the
  tenant of the call and returns an operation handle (AB#8445, Feature AB#5694 F3.6). Optional parameter
  `includeArchiveData` (Boolean); result record `RepositoryDumpResult` (`JobId`, `DownloadPath`); declared errors
  `DUMP_FAILED` and `DUMP_INTERRUPTED`.
- Record `RepositoryDumpResult` and the attributes `RepositoryDump.JobId` and `RepositoryDump.DownloadPath`.

### Compatibility

- Additions only: no element removed or changed, `ckLanguage` stays 2, dependency `System-[2.5,3.0)` unchanged.
  Dependents (`System.Communication` 4.x, `System.Ai` 4.x, `Loxone` 5.x) resolve as before (range retention).

## 3.6.0 - 2026-11-09

_Required bump level: minor._

### Changed

- Model 'System.Bot': ckLanguage changed '1' → '2' _(minor — CK language version raised; older engines reject the model
  with a clear error)_ (AB#8593, Feature AB#5697 F4.3).
- `AttributeAggregateConfiguration` and `Fixup` declare `derivable: Any` explicitly. ckLanguage 2 changes the default to
  `Model`; without the explicit value both types would be a MAJOR change (row T6, `OCTO-CK200`).

### Compatibility

- The compiled model equals 3.5.0 after the version, `ckLanguage`, `minEngineVersion`, `derivable` and the resolved
  dependency closure are removed (same normalised SHA-256). Ids, attributes, defaults, ownership and the association
  role are unchanged; `visibility` stays `Public` and attribute `access` stays `ReadWrite`.
- `minEngineVersion` 3.5.1 (the CK v2 minimum), published under `ck-models/v3/` only.
- Dependents: `System.Communication` 4.x, `System.Ai` 4.x and `Loxone` 5.x are Compatible (range retention, no rebuild or
  bump). The exact-pinned legacy majors (`System.Communication` 2.x/3.x, `System.Ai` 3.x, `Loxone` 4.x) stay pinned to
  3.4.0 or 3.1.0 as before.
- Not in this release: the System interfaces (`Named-1`, `Enableable-1`) need `System` 2.7, and raising the floor of
  `System-[2.5,3.0)` is a MAJOR (row D1, the published 3.5.0 resolved System 2.5.0). No attribute is tightened: no
  System.Bot attribute is `securitySensitive`, so a tightening would be a MAJOR (row T7).
