using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Services.ArtifactStorage;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Services;

/// <summary>
///     AB#6969 — nested objects of the copy staging area in the bot's artifact storage: the run folder, its deletion,
///     the retention of the hourly cleanup, and the key-id retention check that must ignore them.
/// </summary>
public class BotArtifactStorageStagingTests : IDisposable
{
    private const string Tenant = "Sbeg";
    private readonly ArtifactTestEnvironment _env = new("k1");

    public void Dispose()
    {
        _env.Dispose();
    }

    private async Task PutAsync(string runId, string relativePath, byte[]? content = null, string tenant = Tenant)
    {
        await _env.Storage.PutStagingAsync(tenant, runId, relativePath, new MemoryStream(content ?? [1, 2, 3]));
    }

    private static async Task<List<StagingObject>> ListAsync(IBotArtifactStorage storage, string runId,
        string tenant = Tenant)
    {
        var list = new List<StagingObject>();
        await foreach (var item in storage.ListStagingAsync(tenant, runId))
        {
            list.Add(item);
        }

        return list;
    }

    [Test]
    public async Task Staging_PutListOpen_RoundTripsBytesUnencrypted()
    {
        var content = new byte[] { 9, 8, 7, 6, 5 };

        await PutAsync("r1", "rt1/data/slice-1/part-0.json.gz", content);

        var listed = await ListAsync(_env.Storage, "r1");
        await Assert.That(listed.Count).IsEqualTo(1);
        await Assert.That(listed[0].RelativePath).IsEqualTo("rt1/data/slice-1/part-0.json.gz");
        await Assert.That(listed[0].Size).IsEqualTo(5L);
        await using var read = await _env.Storage.OpenStagingReadAsync(Tenant, "r1", "rt1/data/slice-1/part-0.json.gz");
        using var buffer = new MemoryStream();
        await read!.CopyToAsync(buffer);
        await Assert.That(buffer.ToArray().SequenceEqual(content)).IsTrue();
        await using var raw = await _env.Store.OpenReadAsync(_env.Keys.BuildNested(ArtifactCategories.CopyStaging, Tenant,
            "r1", "rt1/data/slice-1/part-0.json.gz"));
        using var rawBuffer = new MemoryStream();
        await raw!.CopyToAsync(rawBuffer);
        await Assert.That(rawBuffer.ToArray().SequenceEqual(content)).IsTrue();
        await Assert.That(await _env.Storage.OpenStagingReadAsync(Tenant, "r1", "missing/x.gz")).IsNull();
    }

    [Test]
    public async Task DeleteRunAsync_RemovesEveryObjectBelowTheRun_ReturnsTheCount_AndASecondCallReturnsZero()
    {
        await PutAsync("r1", "rt1/data/s1/p0.json.gz");
        await PutAsync("r1", "rt1/data/s2/p0.json.gz");
        await PutAsync("r1", "rt2/genmap/all/p0.json.gz");
        await PutAsync("r10", "rt1/data/s1/p0.json.gz");
        await PutAsync("r1", "rt1/data/s1/p0.json.gz", tenant: "other");
        var dump = _env.WriteFile("dump.zip", [1]);
        await _env.Storage.StoreFileAsync(ArtifactCategories.TenantDumps, Tenant, "dump.zip", dump,
            ArtifactEncryption.None);

        var first = await _env.Storage.DeleteRunAsync(Tenant, "r1");
        var second = await _env.Storage.DeleteRunAsync(Tenant, "r1");

        await Assert.That(first).IsEqualTo(3);
        await Assert.That(second).IsEqualTo(0);
        await Assert.That((await ListAsync(_env.Storage, "r1")).Count).IsEqualTo(0);
        await Assert.That((await ListAsync(_env.Storage, "r10")).Count).IsEqualTo(1);
        await Assert.That((await ListAsync(_env.Storage, "r1", "other")).Count).IsEqualTo(1);
        await Assert.That(await _env.Storage.GetInfoAsync(ArtifactCategories.TenantDumps, Tenant, "dump.zip")).IsNotNull();
    }

    [Test]
    public async Task Cleanup_DeletesNestedRunsOlderThanTheRetention_NamesTheRun_AndKeepsYoungerRunsAndOtherCategories()
    {
        await PutAsync("old-run", "rt1/data/s1/p0.json.gz");
        await PutAsync("old-run", "rt1/genmap/all/p0.json.gz");
        await PutAsync("young-run", "rt1/data/s1/p0.json.gz");
        var dump = _env.WriteFile("dump.zip", [1]);
        await _env.Storage.StoreFileAsync(ArtifactCategories.TenantDumps, Tenant, "young.zip", dump,
            ArtifactEncryption.None);
        foreach (var path in new[] { "rt1/data/s1/p0.json.gz", "rt1/genmap/all/p0.json.gz" })
        {
            _env.AgeKey(_env.Keys.BuildNested(ArtifactCategories.CopyStaging, Tenant, "old-run", path),
                TimeSpan.FromHours(30));
        }

        var logger = new CapturingLogger<CleanupStaleFilesJob>();
        var files = Substitute.For<IBackupFileStorageService>();
        var job = new CleanupStaleFilesJob(logger, files, 4, 7, null, null, _env.Storage, 24);

        await job.Run(null);

        await Assert.That((await ListAsync(_env.Storage, "old-run")).Count).IsEqualTo(0);
        await Assert.That((await ListAsync(_env.Storage, "young-run")).Count).IsEqualTo(1);
        await Assert.That(await _env.Storage.GetInfoAsync(ArtifactCategories.TenantDumps, Tenant, "young.zip")).IsNotNull();
        var deletedLine = logger.Messages.Single(m => m.Contains("copy staging run"));
        await Assert.That(deletedLine.Contains("old-run")).IsTrue();
        await Assert.That(deletedLine.Contains("sbeg")).IsTrue();
        await Assert.That(deletedLine.StartsWith("Deleted 2 object(s)", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Cleanup_RemovesTheLeftoversOfACrashedRunAfterTheRetention()
    {
        // A crashed dump: objects in the staging area and no job. The next hourly cleanup removes them once they are old enough.
        await PutAsync("crashed", "rt1/data/s1/p0.json.gz");
        var job = new CleanupStaleFilesJob(Substitute.For<ILogger<CleanupStaleFilesJob>>(),
            Substitute.For<IBackupFileStorageService>(), 4, 7, null, null, _env.Storage, 24);

        await job.Run(null);
        await Assert.That((await ListAsync(_env.Storage, "crashed")).Count).IsEqualTo(1);

        _env.AgeKey(_env.Keys.BuildNested(ArtifactCategories.CopyStaging, Tenant, "crashed", "rt1/data/s1/p0.json.gz"),
            TimeSpan.FromHours(25));
        await job.Run(null);

        await Assert.That((await ListAsync(_env.Storage, "crashed")).Count).IsEqualTo(0);
    }

    [Test]
    public async Task KeyIdRetentionCheck_IgnoresNestedStagingObjects_WithoutExceptionOrWarning()
    {
        // Staging objects can carry any name, also one that looks like an encrypted file.
        await PutAsync("r1", "rt1/data/s1/looks-encrypted.octoenc", [0x4f, 0x43, 0x54, 0x4f, 0x45, 0x4e, 0x43, 0x31, 1, 0]);
        await PutAsync("r1", "rt1/data/s1/p0.json.gz");
        var dump = _env.WriteFile("dump.zip", new byte[1000]);
        var stored = await _env.Storage.StoreFileAsync(ArtifactCategories.TenantDumps, Tenant, "dump.zip", dump,
            ArtifactEncryption.Required);
        var logger = new CapturingLogger<BotArtifactStorage>();
        var storage = new BotArtifactStorage(_env.Store, _env.Keys, _env.Protector, _env.ScratchDirectory, logger);

        var headers = await storage.GetEncryptedArtifactHeadersAsync();

        await Assert.That(headers.Count).IsEqualTo(1);
        await Assert.That(headers[0].FileName).IsEqualTo(stored.FileName);
        await Assert.That(logger.Messages.Any(m => m.Contains("header", StringComparison.OrdinalIgnoreCase))).IsFalse();
        await Assert.That(logger.Warnings).IsEqualTo(0);
    }

    [Test]
    public async Task DeleteOlderThanNestedAsync_ReturnsTenantAndRunOfEveryDeletedObject()
    {
        await PutAsync("r1", "rt1/data/s1/p0.json.gz");
        await PutAsync("r2", "rt1/data/s1/p0.json.gz", tenant: "other");
        _env.AgeKey(_env.Keys.BuildNested(ArtifactCategories.CopyStaging, "other", "r2", "rt1/data/s1/p0.json.gz"),
            TimeSpan.FromHours(5));

        var deleted = await _env.Storage.DeleteOlderThanNestedAsync(ArtifactCategories.CopyStaging, TimeSpan.FromHours(1));

        await Assert.That(deleted.Count).IsEqualTo(1);
        await Assert.That(deleted[0].TenantId).IsEqualTo("other");
        await Assert.That(deleted[0].RunId).IsEqualTo("r2");
        await Assert.That(deleted[0].RelativePath).IsEqualTo("rt1/data/s1/p0.json.gz");
        await Assert.That((await ListAsync(_env.Storage, "r1")).Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("../x")]
    [Arguments("a//b")]
    [Arguments("a\\b")]
    public async Task Staging_RejectsInvalidRelativePaths(string relativePath)
    {
        await Assert.That(async () => await PutAsync("r1", relativePath)).Throws<ArgumentException>();
    }
}

/// <summary>Collects the rendered log messages of a test.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<string> _messages = [];
    private int _warnings;

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_messages)
            {
                return _messages.ToList();
            }
        }
    }

    public int Warnings => _warnings;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning)
        {
            Interlocked.Increment(ref _warnings);
        }

        lock (_messages)
        {
            _messages.Add(formatter(state, exception));
            if (exception != null)
            {
                _messages.Add(exception.ToString());
            }
        }
    }
}
