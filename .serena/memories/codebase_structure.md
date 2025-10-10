# Codebase Structure: octo-bot-services

## Repository Root

```
octo-bot-services/
├── src/                      # Source code
├── tests/                    # Test projects
├── assets/                   # Images and assets (e.g., meshmakers64.png)
├── devops-build/             # CI/CD pipeline definitions
│   └── azure-pipelines.yml   # Azure DevOps pipeline
├── .github/                  # GitHub configuration
├── .idea/                    # JetBrains Rider configuration
├── bin/                      # Build output (gitignored)
├── Octo.Bots.sln             # Main solution file
├── Directory.Build.props     # MSBuild properties for all projects
├── CLAUDE.md                 # Developer guidance for Claude Code
└── LICENSE                   # License file
```

## Source Projects (`src/`)

### BotServices (`src/BotServices/`)
**Purpose**: Main ASP.NET Core web application

**Key Files:**
- `Program.cs` - Application entry point, configures services, Hangfire, authentication
- `appsettings.json` - Configuration settings
- `appsettings.Development.json` - Development overrides
- `nlog.config` - NLog logging configuration
- `Dockerfile` - Container image definition

**Responsibilities:**
- Host the REST API
- Configure and run Hangfire server
- Expose Swagger UI (`/swagger`)
- Expose Hangfire Dashboard (`/hangfire`)
- Configure JWT + OAuth2 authentication
- Register all background jobs

### Jobs (`src/Jobs/`)
**Purpose**: Background job implementations (packaged as NuGet)

**Package Info:**
- Assembly: `Meshmakers.Octo.Backend.Jobs`
- Packable: Yes (distributed as NuGet package)
- Dependencies: Hangfire.Core, RestSharp, Meshmakers.Octo.Services.Contracts

**Job Types:**
- `DumpRepositoryJob` - MongoDB database dumps
- `RestoreRepositoryJob` - MongoDB database restores
- `ImportModelJob` - Import data models
- `ExportModelJob` - Export data models
- `ServiceHookJob` - Execute service hooks
- `RunFixupJob` - Repository fixup operations
- `AttributeValueAggregatorJob` - Aggregate attribute values

**Pattern**: Each job has an interface (`I{JobName}Job`) and implementation.

### RepositoryUpdate (`src/RepositoryUpdate/`)
**Purpose**: MongoDB repository management utilities

**Key Features:**
- MongoDB dump/restore operations
- TypeScript scripts for MongoDB operations
- npm project with TypeScript support
- Type checking via `npm run type-check`

**Files:**
- TypeScript files (`.ts`) for MongoDB scripts
- `package.json` - npm dependencies
- `tsconfig.json` - TypeScript configuration

### SystemBotCkModel (`src/SystemBotCkModel/`)
**Purpose**: System bot construction kit data models

**Responsibilities:**
- Define data models for system bots
- Construction kit schema definitions

### BotServices.Resources (`src/BotServices.Resources/`)
**Purpose**: Localized resource files

**Responsibilities:**
- Internationalization (i18n) resources
- Localized strings for the bot services

## Test Projects (`tests/`)

### RepositoryUpdate.Tests (`tests/RepositoryUpdate.Tests/`)
**Purpose**: Unit tests for RepositoryUpdate services

**Test Type**: Unit tests (fast, no external dependencies)

**Framework**: xUnit + Moq + FluentAssertions

### RepositoryUpdate.IntegrationTests (if exists)
**Purpose**: Integration tests requiring MongoDB tools

**Prerequisites:**
- `mongosh` - MongoDB Shell
- `mongodump` - MongoDB dump utility
- `mongorestore` - MongoDB restore utility

**Marker**: Tests use `[Trait("Category", "Integration")]`

## Build Configuration

### Directory.Build.props
Defines global MSBuild properties:
- **TargetFramework**: `net9.0`
- **LangVersion**: `latestmajor`
- **Nullable**: Enabled
- **TreatWarningsAsErrors**: True
- **ImplicitUsings**: True
- **OctoVersion**: 
  - `999.0.0` for DebugL
  - `0.1.*` for private NuGet server
  - `3.2.*` for public NuGet
- **RestoreSources**: Configures NuGet package sources

### Build Configurations
- **Debug**: Standard development
- **DebugL**: Local development with `999.0.0` version and `../nuget` feed
- **Release**: Production build

## CI/CD (`devops-build/`)

### azure-pipelines.yml
- **Build Pool**: meshmakers-ci-agents
- **Target**: .NET 9.0
- **Test Exclusions**: `*SystemTests.csproj`
- **Artifacts**: Docker images
- **Branches**: `dev/*`, `test/*`, `main`

## Output Structure

All projects output to: `bin\$(Configuration)\`

**Example for DebugL:**
```
bin/
└── DebugL/
    ├── Meshmakers.Octo.Backend.Jobs.dll
    ├── Meshmakers.Octo.Backend.Jobs.xml
    └── [other assemblies]
```
