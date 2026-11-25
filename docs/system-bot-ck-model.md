---
description: System.Bot model: ckLanguage 2 decisions, semver rows, minEngineVersion, and automatic publishing from main.
applies_to: src/SystemBotCkModel/**, tests/Jobs.Tests/CkModels/**
---

# The System.Bot CK model

## System.Bot on ckLanguage 2 (System.Bot 3.6.0)

`src/SystemBotCkModel` is `System.Bot-3.6.0`, the first `ckLanguage: 2` release, a **Minor** against 3.5.0 (dependency
`System-[2.5,3.0)`, range-retaining). Changelog: `src/SystemBotCkModel/ConstructionKit/CHANGELOG.md`.

- **`derivable: Any` on every type.** ckLanguage 2 changes the default from `Any` to `Model`; `AttributeAggregateConfiguration`
  and `Fixup` declare `Any` explicitly, otherwise the switch is a Major (row T6 of `ck-semver-rules.md` in
  `octo-construction-kit-engine`). A **new** type (e.g. the dump job type) defaults to `Model`; declare `derivable: Any`
  only where another model should derive from it.
- **Interfaces: none yet.** `Fixup` carries `Name` and `Enabled` and could implement `System/Named-1` and `System/Enableable-1`,
  but the interfaces exist from System 2.7 on: the compiler refuses them under the floor 2.5 ("elements that do not exist at
  the floor"), and raising the floor to 2.7 is a **Major** (row D1, the published 3.5.0 resolved System 2.5.0).
  `AttributeAggregateConfiguration` assigns `System/Enabled` under the name `IsAutoCompleteEnabled` (error 98 for
  `Enableable-1`; renaming is a Major). Both go with a later major.
- **Access modifiers: none tightened.** Refinery Studio (`fixup-scripts-form`) writes `Name`, `Script`, `Enabled`, `IsApplied`
  and `Order`, and octo-cli `CreateFixupScript` sends the generated `RtFixupDto` through generic GraphQL. `AppliedAt`,
  `IsSuccess`, `Output` and `Error` are written only by `RepositoryFixupService` (repository write, not guarded), but none of
  them is `securitySensitive`, so `ReadOnly` would be a Major (row T7; the first-release exception T7a needs the marker). No
  System.Bot attribute holds credentials.
- **Methods** (`methods:` on a type, `execution.mode: async`) are now possible in this model; a new type or method is a Minor.
- **Rollout.** `minEngineVersion` 3.5.1, published under `ck-models/v3/` only. The service embeds the model and imports it on
  tenant resolve; the embedded-import downgrade guard keeps a newer installed System.Bot when an older bot service starts.
  Publishing follows the main-publish rule below.
- Tests: `tests/Jobs.Tests/CkModels/SystemBotCkLanguage2Tests` (embedded model is 3.6.0 / ckLanguage 2 / minEngineVersion,
  `derivable: Any` on both types, no tightened attribute) and `Jobs.IntegrationTests/SystemBotCkLanguage2PersistenceTests`
  (Docker: import into a tenant, re-import of the same version succeeds, the CK cache rebuilt from MongoDB still has
  `derivable: Any`; `ExportEngineFixture` registers the embedded System.Bot model for it).

## Publishing the system CK model: automatic from main

A merge to `main` publishes `System.Bot` to the private CK catalog automatically (decision 2026-11-10). The pipeline
parameter `publishSystemCkModels` (boolean, **default true**; gate introduced as default false) feeds
`publishOnMain` of `validate-and-publish-ck-versions.yml` (`tpl-v1.8.0` and later; pinned: `tpl-v1.11.0`). ValidateVersion, the schema compile and the cascade check
run on every build and refuse a breaking change declared as minor; an already-published version is a no-op. Default true because CK v2
range retention and the completed re-pin removed the exact-pin breakage that motivated default false.

**To build main without publishing:** Azure DevOps -> the CI pipeline of this repository -> *Run pipeline* -> branch `main` -> untick
*Publish System CK models to the CK catalogs (main)*; CLI: `az pipelines run --id <pipeline id> --branch main --parameters publishSystemCkModels=false`.
`r*` tags (release train) publish as before; `dev/*`/`test/*` builds never publish to the shared catalogs.
