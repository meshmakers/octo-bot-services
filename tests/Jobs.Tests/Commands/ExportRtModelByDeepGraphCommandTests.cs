using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Runtime.Contracts.TransportContainer;
using Meshmakers.Octo.Runtime.Contracts.TransportContainer.DTOs;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Commands;

public class ExportRtModelByDeepGraphCommandTests
{
    private const string TenantId = "tenant-1";

    private readonly ILogger<ExportRtModelByDeepGraphCommand> _logger =
        Substitute.For<ILogger<ExportRtModelByDeepGraphCommand>>();

    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();
    private readonly ICkCacheService _ckCacheService = Substitute.For<ICkCacheService>();

    private readonly IRtEntityToTcDtoConverter _rtEntityToDtoConverter =
        Substitute.For<IRtEntityToTcDtoConverter>();

    private readonly IRtSerializer _rtSerializer = Substitute.For<IRtSerializer>();
    private readonly ITenantRepository _tenantRepository = Substitute.For<ITenantRepository>();
    private readonly IOctoSession _session = Substitute.For<IOctoSession>();

    private ExportRtModelByDeepGraphCommand CreateCommand()
    {
        return new ExportRtModelByDeepGraphCommand(_logger, _systemContext, _ckCacheService,
            _rtEntityToDtoConverter, _rtSerializer);
    }

    private void SetupInstalledModels(params CkModel[] models)
    {
        var resultSet = Substitute.For<IResultSet<CkModel>>();
        resultSet.Items.Returns(models);
        _tenantRepository.GetCkModelsAsync(Arg.Any<IOctoSession>(), Arg.Any<List<CkModelId>?>(),
            Arg.Any<RtEntityQueryOptions>(), Arg.Any<int?>(), Arg.Any<int?>()).Returns(resultSet);
    }

    private RtEntityTcDto SetupEntityOfModel(string modelName, string modelVersion, string typeName)
    {
        var entity = new RtEntityTcDto { CkTypeId = new RtCkId<CkTypeId>($"{modelName}/{typeName}") };
        var typeGraph = new CkTypeGraph(new CkId<CkTypeId>($"{modelName}-{modelVersion}/{typeName}"),
            new CkCompiledTypeDto());
        _ckCacheService.GetRtCkType(TenantId, entity.CkTypeId).Returns(typeGraph);
        return entity;
    }

    private static CkModel CreateModel(string name, string version, params CkModelId[] dependencies)
    {
        return new CkModel
        {
            Id = new CkModelId(name, version),
            ModelId = name,
            Dependencies = dependencies
        };
    }

    [Test]
    public async Task DetermineModelDependenciesAsync_TransitiveChain_ResolvesAllModels()
    {
        // Arrange: exported entity belongs to ModelA; installed models form the chain A -> B -> C
        var entity = SetupEntityOfModel("ModelA", "1.0.0", "TypeX");
        SetupInstalledModels(
            CreateModel("ModelA", "1.0.0", new CkModelId("ModelB", "1.1.0")),
            CreateModel("ModelB", "1.1.0", new CkModelId("ModelC", "2.0.0")),
            CreateModel("ModelC", "2.0.0"));

        var command = CreateCommand();

        // Act
        var result = await command.DetermineModelDependenciesAsync(TenantId, _tenantRepository,
            _session, [entity]);

        // Assert: seeding A must yield A, B and C
        var fullNames = result.Select(r => r.SemanticVersionedFullName).ToList();
        await Assert.That(result.Count).IsEqualTo(3);
        await Assert.That(fullNames).Contains("ModelA-[1.0,2.0)");
        await Assert.That(fullNames).Contains("ModelB-[1.1,2.0)");
        await Assert.That(fullNames).Contains("ModelC-[2.0,3.0)");
    }

    [Test]
    public async Task DetermineModelDependenciesAsync_CyclicDependencies_Terminates()
    {
        // Arrange: A -> B and B -> A form a cycle; the walk must terminate and yield both
        var entity = SetupEntityOfModel("ModelA", "1.0.0", "TypeX");
        SetupInstalledModels(
            CreateModel("ModelA", "1.0.0", new CkModelId("ModelB", "1.0.0")),
            CreateModel("ModelB", "1.0.0", new CkModelId("ModelA", "1.0.0")));

        var command = CreateCommand();

        // Act
        var result = await command.DetermineModelDependenciesAsync(TenantId, _tenantRepository,
            _session, [entity]);

        // Assert
        var names = result.Select(r => r.Name).ToList();
        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(names).Contains("ModelA");
        await Assert.That(names).Contains("ModelB");
    }

    [Test]
    public async Task DetermineModelDependenciesAsync_SystemModelDependency_IsExcluded()
    {
        // Arrange: ModelA depends on the System model, which is always available and must be excluded
        var entity = SetupEntityOfModel("ModelA", "1.0.0", "TypeX");
        SetupInstalledModels(
            CreateModel("ModelA", "1.0.0", new CkModelId("System", "1.0.0")),
            CreateModel("System", "1.0.0"));

        var command = CreateCommand();

        // Act
        var result = await command.DetermineModelDependenciesAsync(TenantId, _tenantRepository,
            _session, [entity]);

        // Assert
        var names = result.Select(r => r.Name).ToList();
        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(names).Contains("ModelA");
    }
}
