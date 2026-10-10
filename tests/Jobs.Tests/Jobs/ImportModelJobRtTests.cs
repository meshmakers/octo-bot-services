using Meshmakers.Common.Shared.Services;
using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Common.DistributionEventHub.Payloads;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs;

/// <summary>
///     AB#6392 — the ImportRt job runs the import on behalf of the initiating caller: the engine command is called
///     with a user-initiated caller context (blueprint-lock protection), never as a plain system import, also for
///     jobs enqueued without a caller and for a missing subject.
/// </summary>
public class ImportModelJobRtTests
{
    private readonly ILogger<ImportModelJob> _logger = Substitute.For<ILogger<ImportModelJob>>();
    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();
    private readonly IDistributedCacheService _cacheService = Substitute.For<IDistributedCacheService>();
    private readonly IImportRtModelCommand _importRtModelCommand = Substitute.For<IImportRtModelCommand>();
    private readonly ITenantRepository _tenantRepository = Substitute.For<ITenantRepository>();

    private ImportModelJob CreateJob()
    {
        _importRtModelCommand.BlueprintLockSummary.Returns(RtImportBlueprintLockSummary.Empty);
        _systemContext.FindTenantRepositoryAsync("tenant-1").Returns(_tenantRepository);
        _cacheService.GetCacheStreamByIdAsync("tenant-1", "key-1").Returns(new CacheStream
        {
            Stream = new MemoryStream("{}"u8.ToArray()), ContentType = "application/json", FileName = "rt.json"
        });
        return new ImportModelJob(_logger, _systemContext, _cacheService, Substitute.For<ICompressionService>(),
            Substitute.For<IImportCkModelCommand>(), _importRtModelCommand);
    }

    [Test]
    public async Task ImportRtAsync_RunsOnBehalfOfTheInitiatingCaller()
    {
        await CreateJob().ImportRtAsync("tenant-1", ImportStrategy.Upsert, "key-1", "user-sub-1", null);

        await _importRtModelCommand.Received(1).ImportAsCallerAsync(_tenantRepository, Arg.Any<string>(),
            Arg.Any<string>(), ImportStrategy.Upsert,
            Arg.Is<RtImportCallerContext>(c => c.EnforceBlueprintLock && c.SubjectId == "user-sub-1"),
            Arg.Any<CancellationToken?>(), Arg.Any<RtImportBlankingPolicy>(),
            Arg.Any<IReadOnlyCollection<RtImportBlankingConfirmation>?>());
        await _importRtModelCommand.DidNotReceive().ImportAsync(Arg.Any<Meshmakers.Octo.Runtime.Contracts.Repositories.IRuntimeRepository>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ImportStrategy>(), Arg.Any<CancellationToken?>(),
            Arg.Any<RtImportBlankingPolicy>(), Arg.Any<IReadOnlyCollection<RtImportBlankingConfirmation>?>());
        await _cacheService.Received(1).DeleteCacheStreamAsync("tenant-1", "key-1");
    }

    [Test]
    public async Task ImportRtAsync_JobWithoutCaller_IsStillProtected_NotSystem()
    {
        // overload kept for jobs enqueued before the caller was passed along
        await CreateJob().ImportRtAsync("tenant-1", ImportStrategy.Insert, "key-1", null);

        await _importRtModelCommand.Received(1).ImportAsCallerAsync(_tenantRepository, Arg.Any<string>(),
            Arg.Any<string>(), ImportStrategy.Insert,
            Arg.Is<RtImportCallerContext>(c => c.EnforceBlueprintLock && c.SubjectId == null),
            Arg.Any<CancellationToken?>(), Arg.Any<RtImportBlankingPolicy>(),
            Arg.Any<IReadOnlyCollection<RtImportBlankingConfirmation>?>());
    }

    [Test]
    public async Task ImportRtAsync_BlueprintLockedEntities_FailTheJob_AndKeepTheCacheFile()
    {
        _importRtModelCommand.ImportAsCallerAsync(Arg.Any<Meshmakers.Octo.Runtime.Contracts.Repositories.IRuntimeRepository>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ImportStrategy>(), Arg.Any<RtImportCallerContext>(),
                Arg.Any<CancellationToken?>(), Arg.Any<RtImportBlankingPolicy>(),
                Arg.Any<IReadOnlyCollection<RtImportBlankingConfirmation>?>())
            .Returns<Task>(_ => throw new ExchangeException("Import rejected [6384]: 2 entities are locked by blueprint", 6384));

        var job = CreateJob();

        await Assert.That(async () => await job.ImportRtAsync("tenant-1", ImportStrategy.Upsert, "key-1", "user-sub-1", null))
            .Throws<ExchangeException>();
        await _cacheService.DidNotReceive().DeleteCacheStreamAsync("tenant-1", "key-1");
    }
}
