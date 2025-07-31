# RepositoryUpdate.Tests

Umfassende Unit- und Integrationstests für den CommandExecutionService.

## Test-Struktur

### Unit Tests
- **Services/CommandExecutionServiceTests.cs** - Haupttests für den CommandExecutionService
- **Models/** - Tests für alle Model-Klassen (CommandResult, MongoDumpOptions, MongoRestoreOptions)

### Integration Tests  
- **Integration/CommandExecutionServiceIntegrationTests.cs** - End-to-End Tests mit echten Kommandos

### Test Utilities
- **Utilities/TestUtilities.cs** - Helper-Methoden und Test-Fixtures

## Test-Kategorien

### Unit Tests (Standard)
```bash
dotnet test --filter "Category!=Integration&Category!=Performance"
```

### Integration Tests
Benötigen installierte MongoDB Tools:
```bash
dotnet test --filter "Category=Integration"
```

### Performance Tests
```bash
dotnet test --filter "Category=Performance"
```

### Alle Tests
```bash
dotnet test
```

## Voraussetzungen für Integration Tests

### MongoDB Tools
```bash
# macOS
brew install mongodb/brew/mongodb-database-tools
brew install mongosh

# oder npm
npm install -g mongosh
```

### System Commands
Die Tests verwenden plattformspezifische Kommandos:
- **Unix/macOS**: `echo`, `ls`, `sleep`, `pwd`, `date`, `bash`
- **Windows**: `cmd`, `timeout`, `dir`

## Test Coverage

### CommandExecutionService Tests
- ✅ MongoDB Shell Script Ausführung
- ✅ MongoDB Shell Command Ausführung  
- ✅ MongoDB Dump Operations
- ✅ MongoDB Restore Operations
- ✅ Generische Command Ausführung
- ✅ Connection String Building
- ✅ Logging Verhalten
- ✅ Error Handling
- ✅ Concurrent Execution

### Model Tests
- ✅ CommandResult Factory Methods
- ✅ CommandResult ToString() Formatting
- ✅ MongoDumpOptions Factory Methods
- ✅ MongoRestoreOptions Factory Methods
- ✅ Property Validation

### Integration Tests
- ✅ Echte System Commands
- ✅ MongoDB Tools (falls verfügbar)
- ✅ Error Scenarios
- ✅ Performance Benchmarks
- ✅ Concurrent Operations

## Mocking Strategy

### Dependencies
- **IOptions<OctoSystemConfiguration>** - Gemockt für Unit Tests
- **ILogger<CommandExecutionService>** - Gemockt mit Verifikation
- **System.Diagnostics.Process** - Nicht gemockt, echte Ausführung

### Test Data
- Temporäre Dateien und Verzeichnisse
- Plattformspezifische Commands
- Sichere Cleanup-Mechanismen

## Ausführung in CI/CD

### GitHub Actions Beispiel
```yaml
- name: Run Unit Tests
  run: dotnet test --filter "Category!=Integration" --logger trx --results-directory TestResults

- name: Run Integration Tests (if tools available)
  run: |
    if command -v mongosh &> /dev/null; then
      dotnet test --filter "Category=Integration" --logger trx --results-directory TestResults
    fi
```

### Test Parallelisierung
```xml
<!-- In Directory.Build.props -->
<PropertyGroup>
  <ParallelizeTestCollections>true</ParallelizeTestCollections>
  <ParallelizeAssembly>true</ParallelizeAssembly>
</PropertyGroup>
```

## Debugging Tests

### Visual Studio / Rider
- Setze Breakpoints in Test-Methoden
- Nutze Test Explorer für einzelne Tests

### Command Line
```bash
# Einzelner Test
dotnet test --filter "FullyQualifiedName~ExecuteCommandAsync_WithEchoCommand_ShouldReturnSuccess"

# Test mit Ausgabe
dotnet test --logger "console;verbosity=detailed"

# Coverage Report
dotnet test --collect:"XPlat Code Coverage"
```

## Erweiterte Test-Szenarien

### Custom Commands hinzufügen
```csharp
[Fact]
public async Task ExecuteCustomCommand_ShouldWork()
{
    // Arrange
    var service = TestUtilities.CreateTestService();
    
    // Act  
    var result = await service.ExecuteCommandAsync("your-tool", "arguments");
    
    // Assert
    result.Should().NotBeNull();
    // Add specific assertions
}
```

### Neue MongoDB Operations testen
```csharp
[Fact]
public async Task ExecuteNewMongoOperation_ShouldIncludeCorrectParameters()
{
    // Test new MongoDB operations following existing patterns
}
```

## Troubleshooting

### Tests schlagen fehl
1. **Integration Tests**: Prüfe ob MongoDB Tools installiert sind
2. **Platform Tests**: Manche Commands sind plattformspezifisch
3. **Permissions**: Stelle sicher, dass Temp-Verzeichnisse beschreibbar sind
4. **Timeouts**: Performance Tests können auf langsamen Systemen fehlschlagen

### Häufige Probleme
- **mongosh not found**: `brew install mongosh` oder npm installieren
- **Permission denied**: Temp-Verzeichnis Permissions prüfen  
- **Port in use**: MongoDB Connection String anpassen
- **Flaky tests**: Zeitabhängige Tests können instabil sein

## Best Practices

### Test Naming
- `MethodName_StateUnderTest_ExpectedBehavior`
- Descriptive test names in German/English

### Arrange-Act-Assert Pattern
```csharp
[Fact]
public async Task TestMethod_Scenario_Expected()
{
    // Arrange
    var service = TestUtilities.CreateTestService();
    var input = "test data";
    
    // Act
    var result = await service.MethodAsync(input);
    
    // Assert
    result.Should().NotBeNull();
    result.Success.Should().BeTrue();
}
```

### Resource Cleanup
- Nutze `IDisposable` für temporäre Ressourcen
- `TestUtilities.SafeDelete*` Methoden verwenden
- Try-catch in cleanup code
