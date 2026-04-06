using Meshmakers.Common.Shared.Services;
using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Common.DistributionEventHub.Payloads;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Microsoft.Extensions.Logging;
using NSubstitute;
namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs;

public class ImportModelJobBatchTests
{
    private readonly ILogger<ImportModelJob> _logger = Substitute.For<ILogger<ImportModelJob>>();
    private readonly IDistributedCacheService _cacheService = Substitute.For<IDistributedCacheService>();
    private readonly ICompressionService _compressionService = Substitute.For<ICompressionService>();
    private readonly IImportCkModelCommand _importCkModelCommand = Substitute.For<IImportCkModelCommand>();
    private readonly IImportRtModelCommand _importRtModelCommand = Substitute.For<IImportRtModelCommand>();

    private ImportModelJob CreateJob()
    {
        return new ImportModelJob(_logger, Substitute.For<Meshmakers.Octo.Runtime.Contracts.MongoDb.ISystemContext>(),
            _cacheService, _compressionService, _importCkModelCommand, _importRtModelCommand);
    }

    [Test]
    public async Task ImportCkBatchAsync_ImportsAllModelsSequentially()
    {
        var keys = new List<string> { "key-1", "key-2", "key-3" };
        foreach (var key in keys)
        {
            var stream = new MemoryStream("{}"u8.ToArray());
            var cacheStream = new CacheStream { Stream = stream, ContentType = "application/json", FileName = $"{key}.json" };
            _cacheService.GetCacheStreamByIdAsync("tenant-1", key).Returns(cacheStream);
        }

        var job = CreateJob();
        await job.ImportCkBatchAsync("tenant-1", keys, null);

        // All 3 models must be imported
        await _importCkModelCommand.Received(3).ImportAsync("tenant-1", Arg.Any<string>(),
            Arg.Any<CancellationToken?>());
    }

    [Test]
    public async Task ImportCkBatchAsync_ClearsCache_ForEachModel()
    {
        var keys = new List<string> { "key-1", "key-2" };
        foreach (var key in keys)
        {
            var stream = new MemoryStream("{}"u8.ToArray());
            var cacheStream = new CacheStream { Stream = stream, ContentType = "application/json", FileName = $"{key}.json" };
            _cacheService.GetCacheStreamByIdAsync("tenant-1", key).Returns(cacheStream);
        }

        var job = CreateJob();
        await job.ImportCkBatchAsync("tenant-1", keys, null);

        // Cache must be cleared for each key
        await _cacheService.Received(1).DeleteCacheStreamAsync("tenant-1", "key-1");
        await _cacheService.Received(1).DeleteCacheStreamAsync("tenant-1", "key-2");
    }

    [Test]
    public async Task ImportCkBatchAsync_StopsOnFirstError()
    {
        var keys = new List<string> { "key-1", "key-2", "key-3" };

        // First key works fine
        var stream1 = new MemoryStream("{}"u8.ToArray());
        _cacheService.GetCacheStreamByIdAsync("tenant-1", "key-1")
            .Returns(new CacheStream { Stream = stream1, ContentType = "application/json", FileName = "key-1.json" });

        // Second key fails during import
        var stream2 = new MemoryStream("{}"u8.ToArray());
        _cacheService.GetCacheStreamByIdAsync("tenant-1", "key-2")
            .Returns(new CacheStream { Stream = stream2, ContentType = "application/json", FileName = "key-2.json" });

        var callCount = 0;
        _importCkModelCommand.ImportAsync("tenant-1", Arg.Any<string>(), Arg.Any<CancellationToken?>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 2)
                    throw new Exception("Import failed for second model");
                return Task.CompletedTask;
            });

        var job = CreateJob();

        await Assert.That(async () => await job.ImportCkBatchAsync("tenant-1", keys, null))
            .Throws<Exception>();

        // Only 2 import calls should have been made (stopped at second failure)
        await _importCkModelCommand.Received(2).ImportAsync("tenant-1", Arg.Any<string>(),
            Arg.Any<CancellationToken?>());

        // Third key should never have been fetched from cache
        await _cacheService.DidNotReceive().GetCacheStreamByIdAsync("tenant-1", "key-3");
    }

    [Test]
    public async Task ImportCkBatchAsync_HandlesEmptyList()
    {
        var job = CreateJob();
        await job.ImportCkBatchAsync("tenant-1", [], null);

        // No imports should happen
        await _importCkModelCommand.DidNotReceive().ImportAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken?>());
    }

    [Test]
    public async Task ImportCkBatchAsync_HandlesSingleModel()
    {
        var keys = new List<string> { "single-key" };
        var stream = new MemoryStream("{}"u8.ToArray());
        _cacheService.GetCacheStreamByIdAsync("tenant-1", "single-key")
            .Returns(new CacheStream { Stream = stream, ContentType = "application/json", FileName = "single-key.json" });

        var job = CreateJob();
        await job.ImportCkBatchAsync("tenant-1", keys, null);

        await _importCkModelCommand.Received(1).ImportAsync("tenant-1", Arg.Any<string>(),
            Arg.Any<CancellationToken?>());
        await _cacheService.Received(1).DeleteCacheStreamAsync("tenant-1", "single-key");
    }
}
