# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository Overview

This is the **octo-bot-services** repository, part of the OctoMesh data mesh platform. It contains bot automation services and background job processing functionality for the OctoMesh ecosystem.

## Development Commands

### Building

```bash
# IMPORTANT: Always use DebugL configuration for local development
dotnet build Octo.Bots.sln --configuration DebugL

# Release build (for production)
dotnet build Octo.Bots.sln --configuration Release
```

**Note**: The `DebugL` configuration is required for local development. It sets the version to `999.0.0` and uses the local NuGet feed at `../nuget`.

### Testing

```bash
# Run all tests
dotnet test Octo.Bots.sln --configuration DebugL

# Run unit tests only (exclude integration tests)
dotnet test --filter "Category!=Integration" --configuration DebugL

# Run integration tests (requires MongoDB tools installed)
dotnet test --filter "Category=Integration" --configuration DebugL

# Run a single test
dotnet test --filter "FullyQualifiedName~ClassName.MethodName" --configuration DebugL

# Run tests with detailed output
dotnet test --logger "console;verbosity=detailed" --configuration DebugL

# Generate code coverage
dotnet test --collect:"XPlat Code Coverage" --configuration DebugL
```

### RepositoryUpdate TypeScript Scripts

The `src/RepositoryUpdate` project contains TypeScript scripts for MongoDB operations:

```bash
cd src/RepositoryUpdate
npm install          # Install dependencies
npm run type-check   # Type-check TypeScript files
```

## Architecture Overview

### Project Structure

The solution (`Octo.Bots.sln`) contains these projects:

- **BotServices** (`src/BotServices/`): Main ASP.NET Core web application that hosts the bot services API and Hangfire dashboard
- **BotServices.Resources** (`src/BotServices.Resources/`): Localized resource files for the bot services
- **Jobs** (`src/Jobs/`): Background job implementations using Hangfire (packaged as NuGet package `Meshmakers.Octo.Backend.Jobs`)
- **SystemBotCkModel** (`src/SystemBotCkModel/`): System bot construction kit data models
- **RepositoryUpdate** (`src/RepositoryUpdate/`): MongoDB repository management utilities with TypeScript script support

### Key Technologies

- **Target Framework**: .NET 9.0 (`net9.0`)
- **Language Version**: Latest major C#
- **Nullable Reference Types**: Enabled
- **Treat Warnings as Errors**: true
- **Test Framework**: xUnit with Moq and FluentAssertions
- **Background Jobs**: Hangfire with MongoDB storage (`Hangfire.Mongo`)
- **Authentication**: JWT Bearer + OpenID Connect (IdentityModel.AspNetCore.OAuth2Introspection)
- **Logging**: NLog (configured via `nlog.config`)
- **Database**: MongoDB with Entity Framework Core

### Build Configurations

- **Debug**: Standard development build
- **DebugL**: Local development with version `999.0.0` and local NuGet feed (`../nuget`)
- **Release**: Production build with proper versioning

### Background Job Types

The `Jobs` project contains several Hangfire job implementations:

- **DumpRepositoryJob**: MongoDB database dump operations
- **RestoreRepositoryJob**: MongoDB database restore operations
- **ImportModelJob**: Import data models
- **ExportModelJob**: Export data models
- **ServiceHookJob**: Execute service hook callbacks
- **RunFixupJob**: Run repository fixup operations
- **AttributeValueAggregatorJob**: Aggregate attribute values

Each job follows the interface pattern (`I{JobName}Job` interface + implementation).

### NuGet Dependencies

The project depends on OctoMesh packages with version `$(OctoVersion)`:
- `Meshmakers.Octo.Services.*` (Swagger, Notifications, Infrastructure, Observability)
- `Meshmakers.Octo.Runtime.*` (Engine.MongoDb)
- `Meshmakers.Octo.Services.Contracts`

Version is determined by:
- **DebugL**: `999.0.0` (local development)
- **Private NuGet server**: `0.1.*`
- **Public NuGet**: `3.2.*`

### Entry Point

The main entry point is `src/BotServices/Program.cs`, which:
- Configures NLog logging
- Sets up Hangfire with MongoDB storage
- Configures JWT and OpenID Connect authentication
- Registers background jobs and consumers
- Exposes Swagger UI and Hangfire Dashboard

### Configuration

Application settings are in `src/BotServices/appsettings.json` and `appsettings.Development.json`:
- Bot-specific settings under `"Bot"` section
- System configuration under `"System"` section
- MongoDB connection strings
- Authentication/authorization settings

## Testing Strategy

### Test Projects

- **RepositoryUpdate.Tests** (`tests/RepositoryUpdate.Tests/`): Comprehensive unit tests for RepositoryUpdate services
- **RepositoryUpdate.IntegrationTests** (`tests/RepositoryUpdate.IntegrationTests/`): Integration tests requiring MongoDB tools

### Test Categories

Tests are categorized using xUnit traits:
- **Unit Tests**: Default tests (fast, no external dependencies)
- **Integration Tests**: `[Trait("Category", "Integration")]` - require MongoDB tools
- **Performance Tests**: `[Trait("Category", "Performance")]` - performance benchmarks

### Prerequisites for Integration Tests

Integration tests require MongoDB command-line tools:
- `mongosh` - MongoDB Shell
- `mongodump` - MongoDB dump utility
- `mongorestore` - MongoDB restore utility

### Test Naming Convention

Tests follow the pattern: `MethodName_StateUnderTest_ExpectedBehavior`

## Key Patterns

1. **Hangfire Background Jobs**: All background jobs implement interface-based contracts and are registered in `Program.cs`
2. **Dependency Injection**: Uses Microsoft.Extensions.DependencyInjection throughout
3. **Configuration Options Pattern**: `IOptions<T>` for strongly-typed configuration
4. **Authentication**: OAuth2 introspection + JWT Bearer tokens
5. **Repository Pattern**: MongoDB repositories with construction kit models
6. **TypeScript Integration**: MongoDB scripts with TypeScript type checking for IntelliSense

## CI/CD

The project uses Azure Pipelines (`devops-build/azure-pipelines.yml`):
- Builds on `meshmakers-ci-agents` pool
- Targets .NET 9.0
- Runs unit tests (excludes `*SystemTests.csproj`)
- Builds and pushes Docker images
- Supports branching strategy: `dev/*`, `test/*`, `main`