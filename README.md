# Octo Bot Services

The bot services of OctoMesh: an ASP.NET Core background-processing service that runs the platform's long-running and recurring jobs (model import/export, repository backup/restore, fixups and attribute-value aggregation) on top of Hangfire. The repository also publishes the **System.Bot** construction kit model as a NuGet package.

## Overview

The service (`Meshmakers.Octo.Backend.BotServices`) is a Docker-deployed web host that schedules and executes jobs through Hangfire (backed by MongoDB). It consumes commands from the OctoMesh distribution event hub - importing CK and runtime models, exporting runtime data by query or deep graph - and exposes a versioned system API plus the Hangfire dashboard under `/ui/jobs`. Resumable uploads are handled via the tus.io protocol, and authentication is wired through OpenID Connect / JWT bearer against the OctoMesh identity service.

The jobs themselves live in the reusable `Meshmakers.Octo.Backend.Jobs` library and include:

- Model import/export (`ImportModelJob`, `ExportModelJob`)
- Repository dump and restore (`DumpRepositoryJob`, `RestoreRepositoryJob`)
- Construction-kit fixups (`RunFixupJob`)
- Attribute-value aggregation for autocomplete (`AttributeValueAggregatorJob`)
- Hourly cleanup of stale backup/upload files (`CleanupStaleFilesJob`)

## Published package

`Meshmakers.Octo.ConstructionKit.Models.System.Bot` is the construction kit model `System.Bot-3.1.1` (depending on `System-[2.0,3.0)`). It defines the entity types the bot service operates on:

- **Fixup** - a named, ordered, scriptable migration entity tracking application state (`IsApplied`, `AppliedAt`, `Output`, `Error`, `IsSuccess`).
- **AttributeAggregateConfiguration** - configures autocomplete value aggregation for a target entity attribute (filter regex, result limit) via the `Configures` association.

The service project itself is not packable (`IsPackable=false`); it is shipped as a container image. Only the CK model and the `Jobs` library are produced as NuGet packages.

## Project structure

| Project | Description |
| --- | --- |
| `src/BotServices` | ASP.NET Core service host (not packable). |
| `src/Jobs` | Hangfire job and command implementations (packable library). |
| `src/RepositoryUpdate` | Repository update logic used by the jobs. |
| `src/BotServices.Resources` | Localized resource strings for the service. |
| `src/SystemBotCkModel` | The System.Bot construction kit model (packable). |
| `tests/Jobs.Tests` | Tests for the Jobs library. |

## Build

```bash
dotnet build Octo.Bots.sln
```

## Test

```bash
dotnet test Octo.Bots.sln
```

## Documentation

The complete OctoMesh documentation is available at https://docs.meshmakers.cloud.

## License

Released under the MIT License.
