using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelCatalogs;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.MongoDb;

namespace Meshmakers.Octo.Backend.Jobs.IntegrationTests;

/// <summary>
///     Resolver stub with a mutable policy table: a test sets <see cref="Table" /> to activate enforcement and resets it
///     afterwards. The loader (System.Identity entity reads) is covered elsewhere; here the enforcement of the real
///     engine under the export jobs is under test (AB#6734).
/// </summary>
internal sealed class TestDataPermissionResolver : IDataPermissionResolver
{
    public RtDataPolicyTable Table { get; set; } = RtDataPolicyTable.Empty;

    public Task<RtDataPolicyTable> GetPolicyTableAsync(IRuntimeRepository runtimeRepository)
    {
        return Task.FromResult(Table);
    }

    public void Invalidate(string tenantId)
    {
    }
}

/// <summary>
///     Records the import audit events of the engine (AB#7450: the missing-mandatory-attributes event) instead of
///     forwarding them to an audit sink, so a test can assert what an import reported.
/// </summary>
internal sealed class RecordingRtImportAuditTrail : IRtImportAuditTrail
{
    private readonly List<(OctoObjectId RtId, IReadOnlyList<string> Missing)> _missing = [];

    public IReadOnlyList<(OctoObjectId RtId, IReadOnlyList<string> Missing)> MissingMandatory
    {
        get
        {
            lock (_missing)
            {
                return _missing.ToArray();
            }
        }
    }

    public void Clear()
    {
        lock (_missing)
        {
            _missing.Clear();
        }
    }

    public Task RecordMissingMandatoryAttributesAsync(string? tenantId, RtCkId<CkTypeId> ckTypeId, OctoObjectId rtId,
        IReadOnlyList<string> missingCkAttributeIds)
    {
        lock (_missing)
        {
            _missing.Add((rtId, missingCkAttributeIds));
        }

        return Task.CompletedTask;
    }

    public Task RecordSkippedDanglingEdgesAsync(string? tenantId, int skippedCount,
        IReadOnlyList<string> sampleEdgeDescriptions) => Task.CompletedTask;

    public Task RecordReplacedToOneEdgesAsync(string? tenantId, int replacedCount,
        IReadOnlyList<string> sampleEdgeDescriptions) => Task.CompletedTask;
}

/// <summary>
///     One real engine on one real MongoDB (Testcontainers replica set) for the whole test process: the BSON class maps
///     of the driver are process-global and must be registered exactly once. The system tenant carries the System CK
///     model and the small <c>ExportTest</c> model; every test starts from an empty tenant (<see cref="ResetAsync" />).
/// </summary>
internal sealed class ExportEngineFixture
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static ExportEngineFixture? _shared;

    private const string AdminUser = "octo-system-admin";
    private const string AdminPassword = "OctoAdmin1";

    private ExportEngineFixture(ServiceProvider provider, string tenantId, TestDataPermissionResolver resolver,
        RecordingRtImportAuditTrail auditTrail, CapturedLogs logs)
    {
        Logs = logs;
        Provider = provider;
        TenantId = tenantId;
        Resolver = resolver;
        AuditTrail = auditTrail;
    }

    /// <summary>Every log line of the engine at Debug and above (in memory, for credential checks).</summary>
    public CapturedLogs Logs { get; }

    /// <summary>Incremented by every <see cref="ResetAsync" />: a test class that seeds once checks it to notice a reset.</summary>
    public int ResetGeneration { get; private set; }

    /// <summary>The import audit events recorded since the last <see cref="ResetAsync" />.</summary>
    public RecordingRtImportAuditTrail AuditTrail { get; }

    /// <summary>The import options of the engine (singleton); a test may switch strict validation and must restore it.</summary>
    public RtImportOptions ImportOptions => Provider.GetRequiredService<RtImportOptions>();

    public ServiceProvider Provider { get; }

    /// <summary>The id of the system tenant, the tenant the exports run against.</summary>
    public string TenantId { get; }

    public TestDataPermissionResolver Resolver { get; }

    public ISystemContext SystemContext => Provider.GetRequiredService<ISystemContext>();

    public static async Task<ExportEngineFixture> GetAsync()
    {
        if (_shared != null)
        {
            return _shared;
        }

        await Gate.WaitAsync();
        try
        {
            return _shared ??= await CreateAsync();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<ExportEngineFixture> CreateAsync()
    {
        var container = new MongoDbBuilder("mongo:8.0.15")
            .WithReplicaSet()
            .WithName($"mongodb-bot-export-tests-{Guid.NewGuid():N}")
            .WithUsername(AdminUser)
            .WithPassword(AdminPassword)
            .Build();
        using (var startCts = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
        {
            await container.StartAsync(startCts.Token);
        }

        var tenantId = $"botexporttests{Guid.NewGuid():N}";
        var resolver = new TestDataPermissionResolver();
        var auditTrail = new RecordingRtImportAuditTrail();

        var services = new ServiceCollection();
        services.AddRuntimeEngine().AddMongoDbRuntimeRepository();
        services.AddCkModelExportTestV1();
        // The real System.Identity model (credential guard tests); imported only by the tests that need it.
        services.AddTransient<ICkEmbeddedModel, SystemIdentitySnapshotCkModel>();
        // Captured in memory only (nothing reaches the console): Debug includes the MongoDB command log, which the
        // credential guard tests check for credential values (AB#7781).
        var logs = new CapturedLogs();
        services.AddLogging(b => b.ClearProviders().SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        // Last registration wins over the TryAddSingleton in AddRuntimeEngine.
        services.AddSingleton<IDataPermissionResolver>(resolver);
        services.AddSingleton<IRtImportAuditTrail>(auditTrail);
        services.Configure<OctoSystemConfiguration>(t =>
        {
            t.SystemTenantId = tenantId;
            t.SystemDatabaseName = tenantId;
            t.DatabaseHost = $"localhost:{container.GetMappedPublicPort()}";
            t.AdminUser = AdminUser;
            t.AdminUserPassword = AdminPassword;
            t.DatabaseUserPassword = "OctoUser1";
            t.UseDirectConnection = true;
        });

        var provider = services.BuildServiceProvider();
        var systemContext = provider.GetRequiredService<ISystemContext>();
        await systemContext.CreateSystemTenantAsync();

        var fixture = new ExportEngineFixture(provider, tenantId, resolver, auditTrail, logs);
        await fixture.ImportModelAsync();
        return fixture;
    }

    private async Task ImportModelAsync()
    {
        var operationResult = new OperationResult();
        await SystemContext.ImportCkModelAsync(new CkModelId("ExportTest"), operationResult);
        if (operationResult.HasErrors)
        {
            throw new InvalidOperationException("Import of the ExportTest CK model failed: " + operationResult);
        }
    }

    /// <summary>Imports the real System.Identity model (<see cref="SystemIdentitySnapshotCkModel" />) into the tenant.</summary>
    public async Task ImportSystemIdentityAsync()
    {
        var operationResult = new OperationResult();
        await SystemContext.ImportCkModelAsync(new CkModelId(SystemIdentitySnapshotCkModel.ModelFullName), operationResult);
        if (operationResult.HasErrors)
        {
            throw new InvalidOperationException("Import of the System.Identity CK model failed: " + operationResult);
        }
    }

    /// <summary>Empties the tenant (and its policy table) so every test starts from the same state.</summary>
    public async Task ResetAsync()
    {
        Logs.Enabled = false;
        ResetGeneration++;
        Resolver.Table = RtDataPolicyTable.Empty;
        ImportOptions.StrictMandatoryValidation = false;
        AuditTrail.Clear();
        await SystemContext.ClearSystemTenantAsync();
        await ImportModelAsync();
    }
}

/// <summary>
///     In-memory log sink of the test engine; thread-safe. The engine logs at Debug throughout (the MongoDB command log is set up
///     when a repository client is built, so it cannot be switched on later); lines are only kept while <see cref="Enabled" /> is
///     set by a test, and <c>ResetAsync</c> clears the flag.
/// </summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly List<string> _lines = [];
    private volatile bool _enabled;

    /// <summary>Whether lines are kept.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Number of lines captured so far.</summary>
    public int Count
    {
        get
        {
            lock (_lines)
            {
                return _lines.Count;
            }
        }
    }

    /// <summary>The lines captured after <paramref name="start" />.</summary>
    public IReadOnlyList<string> Since(int start)
    {
        lock (_lines)
        {
            return _lines.Skip(start).ToList();
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturedLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!owner.Enabled)
            {
                return;
            }

            lock (owner._lines)
            {
                owner._lines.Add($"{logLevel} {category}: {formatter(state, exception)} {exception}");
            }
        }
    }
}
