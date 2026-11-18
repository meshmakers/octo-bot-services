using Meshmakers.Octo.Backend.BotServices.Services.DumpRegistry;
using Meshmakers.Octo.Backend.Jobs.DumpRegistry;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace Meshmakers.Octo.Backend.Jobs.IntegrationTests;

[NotInParallel]
public class MongoDumpHashRegistryTests
{
    private static readonly DateTime DumpedAtUtc = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);

    private static MongoDbContainer? _container;
    private static MongoClient? _client;

    [Before(Class)]
    public static async Task StartMongoAsync()
    {
        _container = new MongoDbBuilder("mongo:8.0.15")
            .WithName($"mongodb-bot-dump-registry-tests-{Guid.NewGuid():N}")
            .Build();
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await _container.StartAsync(startTimeout.Token);
        _client = new MongoClient(_container.GetConnectionString());
    }

    [After(Class)]
    public static async Task StopMongoAsync()
    {
        _client?.Dispose();
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }

    [Test]
    public async Task RegisteredDump_IsFoundForItsTenantOnly_InAnyTenantCasing()
    {
        var registry = NewRegistry(out _);

        await registry.RegisterAsync(Entry("Tenant-A", "hash-1", "dump-1.tar.gz"));

        await Assert.That(await registry.FindAsync("tenant-a", "hash-1")).IsNotNull();
        await Assert.That(await registry.FindAsync("TENANT-A", "hash-1")).IsNotNull();
        await Assert.That(await registry.FindAsync("tenant-b", "hash-1")).IsNull();
        await Assert.That(await registry.ContainsFileAsync("tenant-a", "dump-1.tar.gz")).IsTrue();
        await Assert.That(await registry.ContainsFileAsync("tenant-b", "dump-1.tar.gz")).IsFalse();
    }

    [Test]
    public async Task SameDumpRegisteredTwice_KeepsOneEntryWithTheLatestExpiry()
    {
        var registry = NewRegistry(out var database);

        await registry.RegisterAsync(Entry("tenant-a", "hash-1", "first.tar.gz"));
        await registry.RegisterAsync(Entry("tenant-a", "hash-1", "second.tar.gz") with
        {
            ExpiresAtUtc = DumpedAtUtc.AddDays(500)
        });

        var collection = database.GetCollection<BsonDocument>(MongoDumpHashRegistry.CollectionName);
        await Assert.That(await collection.CountDocumentsAsync(new BsonDocument())).IsEqualTo(1L);
        var entry = await registry.FindAsync("tenant-a", "hash-1");
        await Assert.That(entry!.ExpiresAtUtc).IsEqualTo(DumpedAtUtc.AddDays(500));
        await Assert.That(entry.FileName).IsEqualTo("second.tar.gz");
    }

    [Test]
    public async Task Indexes_AreUniquePerTenantAndHash_AndExpireOnTheExpiry()
    {
        var registry = NewRegistry(out var database);
        await registry.RegisterAsync(Entry("tenant-a", "hash-1", "dump.tar.gz"));

        var collection = database.GetCollection<BsonDocument>(MongoDumpHashRegistry.CollectionName);
        var indexes = await (await collection.Indexes.ListAsync()).ToListAsync();
        var uniqueIndex = indexes.Single(index => index["name"] == "tenantId_sha256");
        var expiryIndex = indexes.Single(index => index["name"] == "expiresAtUtc_ttl");

        await Assert.That(uniqueIndex["unique"].AsBoolean).IsTrue();
        await Assert.That(expiryIndex["expireAfterSeconds"].ToInt64()).IsEqualTo(0L);
    }

    [Test]
    public async Task TenantPurges_RemoveOnlyTheirEntries()
    {
        var registry = NewRegistry(out _);
        await registry.RegisterAsync(Entry("tenant-a", "old", "old.tar.gz") with
        {
            DumpedAtUtc = DumpedAtUtc.AddDays(-10)
        });
        await registry.RegisterAsync(Entry("tenant-a", "new", "new.tar.gz"));
        await registry.RegisterAsync(Entry("tenant-b", "other", "other.tar.gz"));

        var removedBefore = await registry.DeleteTenantDumpedBeforeAsync("TENANT-A", DumpedAtUtc.AddDays(-1));
        await Assert.That(removedBefore).IsEqualTo(1L);
        await Assert.That(await registry.FindAsync("tenant-a", "new")).IsNotNull();

        var removedAll = await registry.DeleteTenantAsync("tenant-a");
        await Assert.That(removedAll).IsEqualTo(1L);
        await Assert.That(await registry.FindAsync("tenant-b", "other")).IsNotNull();
    }

    private static MongoDumpHashRegistry NewRegistry(out IMongoDatabase database)
    {
        database = _client!.GetDatabase($"jobs-{Guid.NewGuid():N}");
        return new MongoDumpHashRegistry(database);
    }

    private static DumpHashEntry Entry(string tenantId, string sha256, string fileName) =>
        new(tenantId, sha256, DumpedAtUtc, DumpedAtUtc.AddDays(365), fileName, 42, "job-1", DumpHashSources.Dump);
}
