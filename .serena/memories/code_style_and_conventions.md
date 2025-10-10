# Code Style and Conventions: octo-bot-services

## C# Language Settings

- **Language Version**: `latestmajor` - Always use the latest major C# version features
- **Nullable Reference Types**: **Enabled** - All reference types are non-nullable by default; use `?` for nullable
- **Implicit Usings**: **Enabled** - Common namespaces are automatically imported
- **Warnings as Errors**: **Enabled** (`TreatWarningsAsErrors=true`) - All warnings must be fixed

## Naming Conventions

### Test Naming
Tests follow the pattern: `MethodName_StateUnderTest_ExpectedBehavior`

**Examples:**
- `DumpRepository_WithValidParameters_CreatesBackupSuccessfully`
- `RestoreRepository_WithInvalidPath_ThrowsArgumentException`
- `GetJob_WhenJobNotFound_ReturnsNull`

### Interface Pattern for Jobs
All background jobs follow this pattern:
- Interface: `I{JobName}Job` (e.g., `IDumpRepositoryJob`)
- Implementation: `{JobName}Job` (e.g., `DumpRepositoryJob`)

## Project Organization

### Assembly Naming
- Package ID matches assembly name (e.g., `Meshmakers.Octo.Backend.Jobs`)
- Root namespace matches assembly name

### Output Path
All projects output to: `..\..\bin\$(Configuration)\`

### Documentation Files
XML documentation is generated for all projects: `$(AssemblyName).xml`

## Testing Conventions

### Test Categories
Use xUnit traits to categorize tests:

```csharp
[Trait("Category", "Integration")]  // Requires external dependencies (MongoDB tools)
[Trait("Category", "Performance")]  // Performance benchmarks
// No trait = Unit test (fast, no external dependencies)
```

### Test Projects
- **Unit Tests**: `*.Tests` projects (e.g., `RepositoryUpdate.Tests`)
- **Integration Tests**: `*.IntegrationTests` projects
- **System Tests**: `*SystemTests` projects (excluded from CI)

## Dependency Injection

- Use Microsoft.Extensions.DependencyInjection throughout
- Register services in `Program.cs` or extension methods
- Use constructor injection for dependencies
- Follow the Options pattern: `IOptions<T>` for configuration

## Configuration

### Options Pattern
```csharp
public class MySettings
{
    public string Property { get; set; }
}

// In appsettings.json:
{
  "MySettings": {
    "Property": "value"
  }
}

// Registration:
builder.Services.Configure<MySettings>(configuration.GetSection("MySettings"));

// Usage:
public MyService(IOptions<MySettings> options)
{
    var settings = options.Value;
}
```

## Async/Await

- Consistently use async/await patterns
- All I/O operations should be async
- Use `ConfigureAwait(false)` when appropriate in library code

## Design Patterns

1. **Repository Pattern**: MongoDB repositories with construction kit models
2. **Interface-based Contracts**: All jobs implement interfaces
3. **Options Pattern**: Strongly-typed configuration via `IOptions<T>`
4. **Dependency Injection**: Constructor injection throughout
5. **CQRS Elements**: Some command/query separation

## Documentation

- XML documentation comments for public APIs
- Generate XML documentation files (`DocumentationFile` in .csproj)
- Document complex logic inline with comments

## Package Metadata

All packages include:
- `PackageIcon`: meshmakers64.png
- `PackageLicenseFile`: LICENSE
- `PackageProjectUrl`: https://www.meshmakers.io
- `PackageTags`: Octo data mesh iot
- `RepositoryUrl`: https://github.com/meshmakers/octo-bot-services
