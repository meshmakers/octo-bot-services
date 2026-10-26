using Meshmakers.Octo.ConstructionKit.Contracts;
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
        RecordingRtImportAuditTrail auditTrail)
    {
        Provider = provider;
        TenantId = tenantId;
        Resolver = resolver;
        AuditTrail = auditTrail;
    }

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
        services.AddLogging(b => b.ClearProviders().SetMinimumLevel(LogLevel.Warning));
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

        var fixture = new ExportEngineFixture(provider, tenantId, resolver, auditTrail);
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

    /// <summary>Empties the tenant (and its policy table) so every test starts from the same state.</summary>
    public async Task ResetAsync()
    {
        Resolver.Table = RtDataPolicyTable.Empty;
        ImportOptions.StrictMandatoryValidation = false;
        AuditTrail.Clear();
        await SystemContext.ClearSystemTenantAsync();
        await ImportModelAsync();
    }
}
