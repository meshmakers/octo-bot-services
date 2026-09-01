# Octo Bot Services

The bot services of OctoMesh: an ASP.NET Core background-processing service that runs the platform's long-running and recurring jobs (model import/export, repository backup/restore, fixups and attribute-value aggregation) on top of Hangfire. The repository also publishes two NuGet packages: the **System.Bot** construction kit model and the reusable **Jobs** library.

## Overview

The service (`Meshmakers.Octo.Backend.BotServices`) is a Docker-deployed web host that schedules and executes jobs through Hangfire (backed by MongoDB). It consumes commands from the OctoMesh distribution event hub - importing CK and runtime models, exporting runtime data by query or deep graph - and exposes a versioned system API plus the Hangfire dashboard under `/ui/jobs`. Resumable uploads are handled via the tus.io protocol, and authentication is wired through OpenID Connect / JWT bearer against the OctoMesh identity service.

The jobs themselves live in the reusable `Meshmakers.Octo.Backend.Jobs` library and include:

- Model import/export (`ImportModelJob`, `ExportModelJob`)
- Repository dump and restore (`DumpRepositoryJob`, `RestoreRepositoryJob`)
- Construction-kit fixups (`RunFixupJob`)
- Attribute-value aggregation for autocomplete (`AttributeValueAggregatorJob`)
- Hourly cleanup of stale backup/upload files (`CleanupStaleFilesJob`)

Runtime-model exports automatically embed the CK model dependencies required by the exported entities into the transport container. The deep-graph export resolves the full transitive dependency closure (a model's dependencies, their dependencies, and so on) based on the models installed in the tenant, so the exported file lists every model version range the import target must satisfy. The `System` model is omitted because it is always available.

### The tenant gate was a no-op until AB#5054 — and is a structural no-op here anyway

`TenantAuthorizationMiddleware` inspects only principals whose `Identity.AuthenticationType` reads
`Bearer` — its guard against false 403s on the cookie principal this service also issues. That label
comes from `TokenValidationParameters.AuthenticationType`, which the JWT handler leaves at the
framework default `AuthenticationTypes.Federation` unless the host sets it. This service did not, so
the gate returned early on every bearer request. AB#5054 sets it in
`Configuration/ConfigureJwtBearerOptions.cs`.

🔴 It also had to **remove a second configurator**: `Program.cs` passed
`AddJwtBearer(jwt => { jwt.TokenValidationParameters = new TokenValidationParameters { … }; })`,
which — because the options factory runs configurators in registration order — ran after
`ConfigureOptions<ConfigureJwtBearerOptions>()` and replaced the whole instance, discarding both the
explicit `ValidIssuer` and the label. It compiles, and an isolated unit test of the configurator
stays green: octo-ai-services shipped a release in exactly that state (AB#5051 → AB#5056). The rule
is now one configurator owning `Authority`, `Audience`, claim types, issuer and the label, with
`AddJwtBearer()` taking no argument. (The OpenID Connect block in the same file legitimately assigns
its own `TokenValidationParameters` — different options type.)

**In this service the gate still changes nothing, by construction.** Every controller is routed
`system/v{version}/[controller]`; there is **no `{tenantId}` route segment anywhere**, and a job's
target tenant travels as a query argument (`?tenantId=…`) or as TUS upload metadata. The middleware
reads the route value only, so it returns early on every request. The label is set anyway so the
first tenant-scoped route added here arrives gated instead of silently unguarded — which is exactly
the failure AB#5054 exists to remove. For the same reason this service keeps the platform default
`UserTokenEnforcement = Enforce` and does **not** opt down to the `LogOnly` migration mode that
asset-repo and the communication controller use: there is nothing to stage.

Coverage: `tests/Jobs.Tests/Configuration/TenantAuthorizationWiringTests.cs`. This repo had no test
project for the service host at all, so `Jobs.Tests` — its only unit-test assembly — gained a
project reference to `BotServices` plus an `InternalsVisibleTo` entry; the alternative was leaving
the security-relevant wiring of this host permanently untested.

### Tenant authorization for service tokens (AB#5032 / AB#5047)

The request pipeline runs the shared `TenantAuthorizationMiddleware` from `octo-common-services`
(`app.UseOctoTenantAuthorization()` in `Program.cs`, after `UseAuthorization()`): on every
`{tenantId}/...` route it matches the route tenant against the caller's `tenant_id` claim. How
**client-credentials** (service) tokens are treated is operator-settable, and `Program.cs` binds that
setting with `builder.Services.AddOctoTenantAuthorization(builder.Configuration)` — configuration
section `TenantAuthorization`, i.e. the environment variables

| Variable | Values |
| --- | --- |
| `OCTO_TENANTAUTHORIZATION__SERVICETOKENENFORCEMENT` | `Disabled` \| `LogOnly` (default) \| `Enforce` |
| `OCTO_TENANTAUTHORIZATION__CROSSTENANTSERVICECLIENTIDS__0`, `…__1`, … | client ids exempt from the tenant match (expected to stay empty) |

`LogOnly` changes no request outcome but logs every service token that addresses a tenant it was not
issued for; `Enforce` answers those with 403, including a service token carrying no `tenant_id` at
all. The **`Add…` call is what makes the variables take effect** — `UseOctoTenantAuthorization()`
alone runs on the built-in defaults and the environment is ignored (AB#5047). Every OctoMesh service
hosting the middleware (Identity, Communication Controller, Asset-Repo, Bot, MCP) binds the same
section through the same helper, so one fleet-wide value reaches all of them. Semantics and the
`Enforce` rollout rules are documented in `octo-common-services/CLAUDE.md`.

## Published packages

The repository produces two NuGet packages (the service host project itself is `IsPackable=false` and ships as a container image):

- **Meshmakers.Octo.ConstructionKit.Models.System.Bot** - the construction kit model `System.Bot-3.1.1` (depending on `System-[2.0,3.0)`). It defines the entity types the bot service operates on:
  - **Fixup** - a named, ordered, scriptable migration entity tracking application state (`IsApplied`, `AppliedAt`, `Output`, `Error`, `IsSuccess`).
  - **AttributeAggregateConfiguration** - configures autocomplete value aggregation for a target entity attribute (filter regex, result limit) via the `Configures` association.
- **Meshmakers.Octo.Backend.Jobs** - the reusable Hangfire job and command implementations library consumed by the service.

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
