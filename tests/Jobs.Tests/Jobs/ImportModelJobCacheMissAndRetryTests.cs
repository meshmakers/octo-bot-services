using System.Reflection;
using Hangfire;
using Meshmakers.Common.Shared.Services;
using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Common.DistributionEventHub.Payloads;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs;

/// <summary>
///     AB#7256: a cache file that is not there yet is looked up again, a really missing one fails with a typed error
///     naming tenant and key, a failed import job is retried (CK) and never deleted.
/// </summary>
public class ImportModelJobCacheMissAndRetryTests
{
    private readonly IDistributedCacheService _cacheService = Substitute.For<IDistributedCacheService>();
    private readonly IImportCkModelCommand _importCkModelCommand = Substitute.For<IImportCkModelCommand>();

    private ImportModelJob CreateJob()
    {
        return new ImportModelJob(Substitute.For<ILogger<ImportModelJob>>(),
            Substitute.For<Meshmakers.Octo.Runtime.Contracts.MongoDb.ISystemContext>(), _cacheService,
            Substitute.For<ICompressionService>(), _importCkModelCommand,
            Substitute.For<IImportRtModelCommand>(), [TimeSpan.Zero, TimeSpan.Zero]);
    }

    private static CacheStream Json() =>
        new() { Stream = new MemoryStream("{}"u8.ToArray()), ContentType = "application/json", FileName = "m.json" };

    [Test]
    public async Task ImportCkAsync_CacheStreamAppearsOnSecondLookup_Imports()
    {
        _cacheService.GetCacheStreamByIdAsync("tenant-1", "key-1")
            .Returns(Task.FromResult<CacheStream?>(null), Task.FromResult<CacheStream?>(Json()));

        await CreateJob().ImportCkAsync("tenant-1", "key-1", null);

        await _importCkModelCommand.Received(1).ImportAsync("tenant-1", Arg.Any<string>(),
            Arg.Any<CancellationToken?>());
        await _cacheService.Received(2).GetCacheStreamByIdAsync("tenant-1", "key-1");
    }

    [Test]
    public async Task ImportCkAsync_CacheStreamMissing_FailsWithTypedErrorNamingTenantAndKey()
    {
        _cacheService.GetCacheStreamByIdAsync("tenant-1", "key-1").Returns(Task.FromResult<CacheStream?>(null));

        var exception = await Assert.That(async () => await CreateJob().ImportCkAsync("tenant-1", "key-1", null))
            .Throws<JobFailedException>();

        await Assert.That(exception!.Message).Contains("tenant-1").And.Contains("key-1");
        // three lookups in total, nothing imported
        await _cacheService.Received(3).GetCacheStreamByIdAsync("tenant-1", "key-1");
        await _importCkModelCommand.DidNotReceive().ImportAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken?>());
    }

    [Test]
    public async Task ImportCkBatchAsync_FailureInSecondStep_KeepsAllCacheStreamsForTheRetry()
    {
        _cacheService.GetCacheStreamByIdAsync("tenant-1", "key-1").Returns(Task.FromResult<CacheStream?>(Json()));
        _cacheService.GetCacheStreamByIdAsync("tenant-1", "key-2").Returns(Task.FromResult<CacheStream?>(Json()));
        var calls = 0;
        _importCkModelCommand.ImportAsync("tenant-1", Arg.Any<string>(), Arg.Any<CancellationToken?>())
            .Returns(_ => ++calls == 2 ? throw new InvalidOperationException("boom") : Task.CompletedTask);

        await Assert.That(async () => await CreateJob().ImportCkBatchAsync("tenant-1", ["key-1", "key-2"], null))
            .Throws<InvalidOperationException>();

        await _cacheService.DidNotReceive().DeleteCacheStreamAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    [Arguments(nameof(IImportModelJob.ImportCkAsync), 3)]
    [Arguments(nameof(IImportModelJob.ImportCkBatchAsync), 3)]
    [Arguments(nameof(IImportModelJob.ImportRtAsync), 0)]
    public async Task ImportJobs_AreNeverDeleted_AndCkImportsAreRetriedThreeTimes(string method, int attempts)
    {
        var methods = typeof(IImportModelJob).GetMethods().Where(m => m.Name == method).ToList();
        await Assert.That(methods).IsNotEmpty();
        foreach (var m in methods)
        {
            var retry = m.GetCustomAttribute<AutomaticRetryAttribute>();
            await Assert.That(retry).IsNotNull();
            await Assert.That(retry!.Attempts).IsEqualTo(attempts);
            await Assert.That(retry.OnAttemptsExceeded).IsEqualTo(AttemptsExceededAction.Fail);
        }
    }
}
