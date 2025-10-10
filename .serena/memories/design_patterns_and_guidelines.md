# Design Patterns and Guidelines: octo-bot-services

## Core Design Patterns

### 1. Repository Pattern
**Used for**: MongoDB data access

**Structure:**
```csharp
public interface IMyRepository
{
    Task<MyEntity> GetByIdAsync(string id);
    Task<IEnumerable<MyEntity>> GetAllAsync();
    Task AddAsync(MyEntity entity);
    Task UpdateAsync(MyEntity entity);
    Task DeleteAsync(string id);
}

public class MyRepository : IMyRepository
{
    private readonly IMongoDatabase _database;
    
    public MyRepository(IMongoDatabase database)
    {
        _database = database;
    }
    
    // Implementation...
}
```

**Registration:**
```csharp
services.AddScoped<IMyRepository, MyRepository>();
```

### 2. Interface-Based Contracts (Jobs)
**Pattern for all Hangfire background jobs**

**Structure:**
```csharp
// Interface
public interface IDumpRepositoryJob
{
    Task ExecuteAsync(DumpRepositoryRequest request);
}

// Implementation
public class DumpRepositoryJob : IDumpRepositoryJob
{
    private readonly ILogger<DumpRepositoryJob> _logger;
    
    public DumpRepositoryJob(ILogger<DumpRepositoryJob> logger)
    {
        _logger = logger;
    }
    
    public async Task ExecuteAsync(DumpRepositoryRequest request)
    {
        // Job logic...
    }
}
```

**Registration in Program.cs:**
```csharp
builder.Services.AddScoped<IDumpRepositoryJob, DumpRepositoryJob>();
```

**Hangfire Scheduling:**
```csharp
BackgroundJob.Enqueue<IDumpRepositoryJob>(job => job.ExecuteAsync(request));
```

### 3. Options Pattern
**Used for**: Strongly-typed configuration

**Configuration Class:**
```csharp
public class BotSettings
{
    public string ConnectionString { get; set; } = string.Empty;
    public int MaxRetries { get; set; }
    public TimeSpan Timeout { get; set; }
}
```

**appsettings.json:**
```json
{
  "Bot": {
    "ConnectionString": "mongodb://localhost:27017",
    "MaxRetries": 3,
    "Timeout": "00:05:00"
  }
}
```

**Registration:**
```csharp
builder.Services.Configure<BotSettings>(
    builder.Configuration.GetSection("Bot"));
```

**Usage:**
```csharp
public class MyService
{
    private readonly BotSettings _settings;
    
    public MyService(IOptions<BotSettings> options)
    {
        _settings = options.Value;
    }
}
```

### 4. Dependency Injection
**Used throughout the application**

**Guidelines:**
- **Constructor Injection**: Primary DI mechanism
- **Avoid Service Locator**: Don't inject `IServiceProvider`
- **Interface Dependencies**: Depend on abstractions, not implementations
- **Lifetime Management**: 
  - Singleton: Stateless services, caches
  - Scoped: Per-request services (most common)
  - Transient: Lightweight, stateless operations

**Example:**
```csharp
public class MyService
{
    private readonly IRepository _repository;
    private readonly ILogger<MyService> _logger;
    
    public MyService(
        IRepository repository,
        ILogger<MyService> logger)
    {
        _repository = repository;
        _logger = logger;
    }
}
```

### 5. Async/Await Pattern
**Used for**: All I/O operations

**Guidelines:**
- All database operations are async
- All HTTP calls are async
- Use `async Task` instead of `void` (except event handlers)
- Don't block on async code with `.Result` or `.Wait()`
- Use `ConfigureAwait(false)` in library code

**Example:**
```csharp
public async Task<User> GetUserAsync(string id)
{
    return await _repository.GetByIdAsync(id);
}
```

## Architectural Guidelines

### Separation of Concerns

**BotServices Project (Host):**
- ASP.NET Core configuration
- Middleware setup
- Authentication/authorization
- Hangfire configuration
- API controllers (if any)

**Jobs Project (Business Logic):**
- Job implementations
- Business rules
- No ASP.NET Core dependencies
- Packaged as NuGet for reuse

**RepositoryUpdate Project (Data Access):**
- MongoDB operations
- Data migration scripts
- Database utilities

### Error Handling

**Pattern:**
```csharp
public async Task<Result> DoSomethingAsync()
{
    try
    {
        // Operation
        return Result.Success();
    }
    catch (SpecificException ex)
    {
        _logger.LogError(ex, "Specific error occurred");
        throw; // Re-throw or return error result
    }
}
```

**Logging Levels:**
- `LogTrace`: Very detailed debugging
- `LogDebug`: Debugging information
- `LogInformation`: Normal flow events
- `LogWarning`: Abnormal but handled events
- `LogError`: Errors and exceptions
- `LogCritical`: Critical failures

### Background Job Design

**Guidelines:**
1. **Idempotent**: Jobs should be safely retryable
2. **Atomic**: Use transactions where possible
3. **Logged**: Log start, progress, completion, and errors
4. **Monitored**: Expose metrics for Hangfire dashboard
5. **Resilient**: Handle transient failures with retry logic

**Example:**
```csharp
public async Task ExecuteAsync(JobRequest request)
{
    _logger.LogInformation("Starting job {JobId}", request.Id);
    
    try
    {
        // Idempotent operation
        var result = await _service.ProcessAsync(request);
        
        _logger.LogInformation("Job {JobId} completed successfully", request.Id);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Job {JobId} failed", request.Id);
        throw; // Hangfire will retry
    }
}
```

### Configuration Management

**Hierarchy:**
1. `appsettings.json` - Default settings
2. `appsettings.{Environment}.json` - Environment overrides
3. Environment variables - Runtime overrides
4. User secrets - Local development secrets (not in source control)

**Sensitive Data:**
- Never commit connection strings with credentials
- Use Azure Key Vault or environment variables for production
- Use User Secrets for local development

### Testing Patterns

**Unit Test Structure:**
```csharp
public class ServiceTests
{
    [Fact]
    public async Task MethodName_StateUnderTest_ExpectedBehavior()
    {
        // Arrange
        var mock = new Mock<IDependency>();
        mock.Setup(x => x.GetDataAsync()).ReturnsAsync(testData);
        var sut = new Service(mock.Object);
        
        // Act
        var result = await sut.DoSomethingAsync();
        
        // Assert
        result.Should().NotBeNull();
        result.Value.Should().Be(expectedValue);
    }
}
```

**Integration Test Structure:**
```csharp
[Trait("Category", "Integration")]
public class RepositoryIntegrationTests : IDisposable
{
    private readonly MongoDbFixture _fixture;
    
    public RepositoryIntegrationTests()
    {
        _fixture = new MongoDbFixture();
    }
    
    [Fact]
    public async Task Repository_WithRealDatabase_WorksCorrectly()
    {
        // Arrange
        var repository = new Repository(_fixture.Database);
        
        // Act
        await repository.AddAsync(testEntity);
        var result = await repository.GetByIdAsync(testEntity.Id);
        
        // Assert
        result.Should().BeEquivalentTo(testEntity);
    }
    
    public void Dispose()
    {
        _fixture.Dispose();
    }
}
```

## Anti-Patterns to Avoid

❌ **Don't:**
- Block on async code: `task.Result` or `task.Wait()`
- Use `async void` (except event handlers)
- Inject `IServiceProvider` (service locator anti-pattern)
- Catch and swallow exceptions without logging
- Hard-code configuration values
- Use magic strings/numbers
- Create objects with `new` instead of DI
- Mix business logic with infrastructure concerns

✅ **Do:**
- Use async/await throughout
- Use `async Task` for async methods
- Use constructor injection
- Log exceptions with context
- Use configuration system
- Use constants or enums
- Inject dependencies
- Separate concerns clearly
