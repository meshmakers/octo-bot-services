# CK model snapshots for tests

`ck-system.identity-2.30.0.yaml` is the **compiler output** of the `System.Identity` CK model of
octo-identity-services (`src/Persistence.IdentityCkModel/ConstructionKit`, version 2.30.0, `ckLanguage: 2`).
The bot services do not ship System.Identity; the credential guard tests (`IdentityCredentialGuardExportTests`) load this
file as an embedded model (`SystemIdentitySnapshotCkModel`) so the export jobs run against the real access matrix of the
user instead of a test model.

The file is byte-identical to what the compiler writes. Do not edit it by hand.

## Refresh

When System.Identity changes the access of a user attribute, refresh the snapshot:

1. Build octo-identity-services in DebugL (the build compiles the model).
2. Copy `src/Persistence.IdentityCkModel/bin/DebugL/net10.0/octo-ck-libraries/Persistence.IdentityCkModel/out/ck-system.identity-2.yaml`
   to `ck-system.identity-<version>.yaml` here.
3. Update `SystemIdentitySnapshotCkModel.ModelFullName` and the snapshot path if the file name changed.
