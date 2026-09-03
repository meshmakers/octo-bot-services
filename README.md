# Octo Bot Services

The bot services of OctoMesh: an ASP.NET Core background-processing service that runs the platform's long-running and recurring jobs (model import/export, repository backup/restore, fixups and attribute-value aggregation) on top of Hangfire. The repository also publishes two NuGet packages: the **System.Bot** construction kit model and the reusable **Jobs** library.

## Overview

The service (`Meshmakers.Octo.Backend.BotServices`) is a Docker-deployed web host that schedules and executes jobs through Hangfire (backed by MongoDB). It consumes commands from the OctoMesh distribution event hub - importing CK and runtime models, exporting runtime data by query or deep graph - and exposes a versioned system API, a versioned tenant API (`{tenantId}/v1/jobs/...`, AB#5060) plus the Hangfire dashboard under `/ui/jobs`. Resumable uploads are handled via the tus.io protocol, and authentication is wired through OpenID Connect / JWT bearer against the OctoMesh identity service.

The jobs themselves live in the reusable `Meshmakers.Octo.Backend.Jobs` library and include:

- Model import/export (`ImportModelJob`, `ExportModelJob`)
- Repository dump and restore (`DumpRepositoryJob`, `RestoreRepositoryJob`)
- Construction-kit fixups (`RunFixupJob`)
- Attribute-value aggregation for autocomplete (`AttributeValueAggregatorJob`)
- Hourly cleanup of stale backup/upload files (`CleanupStaleFilesJob`)

Runtime-model exports automatically embed the CK model dependencies required by the exported entities into the transport container. The deep-graph export resolves the full transitive dependency closure (a model's dependencies, their dependencies, and so on) based on the models installed in the tenant, so the exported file lists every model version range the import target must satisfy. The `System` model is omitted because it is always available.

### Tenant-routed job operations (AB#5060)

The five job operations whose subject is **one tenant** — repository dump, restore from a tus upload,
archive data export, archive data import and the fixup script run — are served on tenant routes:

| Operation | Route | Removed System route (stage 3) |
| --- | --- | --- |
| Repository dump | `POST {tenantId}/v1/jobs/dump-repository` | ~~`system/v1/jobs/dump-repository?tenantId=…`~~ |
| Restore from upload | `POST {tenantId}/v1/jobs/restore-from-upload` | ~~`system/v1/jobs/restore-from-upload?tenantId=…`~~ |
| Archive data export | `POST {tenantId}/v1/jobs/export-archive-data` | ~~`system/v1/jobs/export-archive-data?tenantId=…`~~ |
| Archive data import | `POST {tenantId}/v1/jobs/import-archive-data-from-upload` | ~~`system/v1/jobs/import-archive-data-from-upload?tenantId=…`~~ |
| Fixup script run | `POST {tenantId}/v1/jobs/run-fixup-scripts` | ~~`system/v1/jobs/run-fixup-scripts?tenantId=…`~~ |

The System variants were **removed** in stage 3 of AB#5060. The checkout was searched for callers
first and none was left: the SDK stopped addressing them when its five job verbs moved to per-call
tenant routes, octo-cli and octo-mcp-service inherit that through the package, and the frontend builds
the tenant route itself.

An external caller still on an old path is refused — but with **403, not 404**, and the reason is
worth knowing. With no System action left to match, `system/v1/jobs/dump-repository` now matches the
tenant route `{tenantId:tenantId}/v1/jobs/dump-repository` with `tenantId = "system"`, so the request
reaches the tenant gate and is refused there because the caller's token names a different tenant.
That is the stricter of the two possible outcomes, and it is what the tests pin. The one consequence
to keep in mind: a tenant *named* `system` would make the old URLs live again as that tenant's
routes.

**Why the route shape is the whole point.** `TenantAuthorizationMiddleware` reads the tenant from the
**route value**. As long as these operations carried their tenant in `?tenantId=…` the gate never saw
them, so a token issued for one tenant could dump, restore or export any other tenant's repository.
No new tenant-addressed operation may be added to the System controller — it holds job-*instance*
operations only.

Both surfaces share their bodies through `Controllers/JobsControllerBase.cs`, so the tenant route
enqueues the identical Hangfire job with identical arguments; `TenantJobRouteAuthorizationTests`
checks that rather than asserting it.

**The five tenant routes carry `[AllowParentTenantAdministration]`** (`octo-common-services`,
AB#5068). An administrator of the **parent** tenant may back up, restore, export and fix up a child
tenant, so a *user* token of the parent passes the gate on the child's route. Service tokens are
never widened by that rule — a client-credentials `tenant_id` proves nothing, because mirrored
clients share the parent's secret. The marker means *administration*, not *access*: none of these
endpoints returns tenant content, and `system/v1/jobs/download` (which does hand out a job artifact)
is deliberately **not** marked. Do not put the marker on anything that reads or writes tenant data.

**The tus upload sink stays tenant-neutral, deliberately.** `/system/v1/tus-upload` requires a
`tenantId` upload-metadata field, but nothing reads it: the file is stored flat under its tus file id
(`BackupFileStorageService.GetTusUploadFilePath`), and both consuming jobs take the tenant from the
request that starts them. A `{tenantId}/v1/tus-upload` route would therefore promise an ownership
binding the storage does not have. The upload is a staging area; the tenant-carrying, gated decision
is the `restore-from-upload` / `import-archive-data-from-upload` call. Binding the sink to a tenant
(persist the metadata, re-check it at consumption time, scope the storage path) is a separate change,
not a route rename.

The `tenantId` route constraint that every other tenant-serving OctoMesh host registers
(`Routing/TenantIdRouteConstraint.cs`, registered in `Program.cs`) arrived with these routes; without
it the `{tenantId:tenantId}` templates never match. While both surfaces existed, `/system/...` won
over `/{tenantId}/...` because literal route segments outrank parameter segments. That precedence is
also why removing the five System actions turns their old URLs into *tenant* routes for a tenant
named `system` rather than into 404s — see the section above.

Coverage: `tests/Jobs.Tests/Api/TenantJobRouteAuthorizationTests.cs` — an in-process TestHost running
the two real controllers behind the real gate: own tenant allowed, parent user token allowed on the
child route, unrelated tenant 403, parent **service** token 403 under `Enforce` while its own tenant
passes, identical enqueued job on both surfaces, and the marker present on the tenant controller and
nowhere else in the assembly.

### The tenant gate was a no-op until AB#5054

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

**At the time of AB#5054 the gate still changed nothing here, by construction:** every controller was
routed `system/v{version}/[controller]`, there was **no `{tenantId}` route segment anywhere**, and a
job's target tenant travelled as a query argument (`?tenantId=…`) or as TUS upload metadata — the
middleware reads the route value only, so it returned early on every request. The label was set
anyway so that the first tenant-scoped route added here would arrive gated instead of silently
unguarded, which is exactly the failure AB#5054 exists to remove. **AB#5060 added those routes** (see
above) and the gate is now live on them.

This service keeps the platform default `UserTokenEnforcement = Enforce` and does **not** opt down to
the `LogOnly` migration mode that asset-repo and the communication controller use: the tenant routes
are new, so there is no installed caller base to stage for, and the deprecated System variants are
untouched by the gate because they carry no route tenant.

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
