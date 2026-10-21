using Hangfire;
using Hangfire.Mongo;
using Hangfire.Storage;
using Meshmakers.Octo.Backend.BotServices.HangfireSetup;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace Meshmakers.Octo.Backend.Jobs.IntegrationTests;

/// <summary>
///     AB#7196 — the Hangfire job storage against a real MongoDB: a missing or older <c>hangfire.schema</c> document must
///     migrate in place (recurring jobs survive, collections are backed up), a newer or unknown one must stop the
///     start with a clear error and leave the data alone. Uses the very factory the bot services use.
/// </summary>
[NotInParallel]
public class HangfireSchemaMigrationTests
{
    private const string AdminUser = "octo-system-admin";
    private const string AdminPassword = "OctoAdmin1";
    private const int RecurringJobCount = 5;

    private static MongoDbContainer? _container;
    private static MongoClient? _client;

    [Before(Class)]
    public static async Task StartMongoAsync()
    {
        _container = new MongoDbBuilder("mongo:8.0.15")
            .WithReplicaSet()
            .WithName($"mongodb-bot-hangfire-tests-{Guid.NewGuid():N}")
            .WithUsername(AdminUser)
            .WithPassword(AdminPassword)
            .Build();
        using var startCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await _container.StartAsync(startCts.Token);
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
    public async Task MissingSchemaDocument_MigratesInPlace_RecurringJobsSurvive()
    {
        var (database, name) = NewDatabase();
        SeedRecurringJobs(name);
        await database.GetCollection<BsonDocument>("hangfire.schema").DeleteManyAsync(new BsonDocument());

        InitializeStorage(database, name);

        await Assert.That(RecurringJobIds(name)).IsEquivalentTo(ExpectedJobIds());
        await Assert.That(await SchemaVersionAsync(database)).IsEqualTo(25);
        await Assert.That(await BackupCollectionNamesAsync(database)).Contains(n => n.StartsWith("hangfire.jobGraph.24."));
    }

    [Test]
    public async Task OlderSchemaVersion_MigratesInPlace_RecurringJobsSurvive()
    {
        var (database, name) = NewDatabase();
        SeedRecurringJobs(name);
        var schema = database.GetCollection<BsonDocument>("hangfire.schema");
        await schema.DeleteManyAsync(new BsonDocument());
        await schema.InsertOneAsync(new BsonDocument("_id", 24));

        InitializeStorage(database, name);

        await Assert.That(RecurringJobIds(name)).IsEquivalentTo(ExpectedJobIds());
        await Assert.That(await SchemaVersionAsync(database)).IsEqualTo(25);
        await Assert.That(await BackupCollectionNamesAsync(database)).Contains(n => n.StartsWith("hangfire.jobGraph.24."));
    }

    [Test]
    public async Task CurrentSchemaVersion_DoesNothing()
    {
        var (database, name) = NewDatabase();
        SeedRecurringJobs(name);

        InitializeStorage(database, name);

        await Assert.That(RecurringJobIds(name)).IsEquivalentTo(ExpectedJobIds());
        await Assert.That(await BackupCollectionNamesAsync(database)).IsEmpty();
    }

    [Test]
    public async Task EmptyDatabase_IsInitialised()
    {
        var (database, name) = NewDatabase();

        InitializeStorage(database, name);

        await Assert.That(await SchemaVersionAsync(database)).IsEqualTo(25);
    }

    [Test]
    public async Task NewerSchemaVersion_StopsWithClearError_AndKeepsData()
    {
        var (database, name) = NewDatabase();
        SeedRecurringJobs(name);
        var schema = database.GetCollection<BsonDocument>("hangfire.schema");
        await schema.DeleteManyAsync(new BsonDocument());
        await schema.InsertOneAsync(new BsonDocument("_id", 26));

        var ex = Assert.Throws<HangfireSchemaException>(() => InitializeStorage(database, name));

        await Assert.That(ex.Message).Contains(name);
        await Assert.That(ex.Message).Contains("schema version 26");
        await Assert.That(ex.Message).Contains("supports up to 25");
        await Assert.That(await SchemaVersionAsync(database)).IsEqualTo(26);
        await Assert.That(await BackupCollectionNamesAsync(database)).IsEmpty();
        await Assert.That(RecurringJobIdsRaw(database)).IsEqualTo(RecurringJobCount);
    }

    [Test]
    public async Task UnknownSchemaMarker_StopsWithClearError_AndKeepsData()
    {
        var (database, name) = NewDatabase();
        SeedRecurringJobs(name);
        var schema = database.GetCollection<BsonDocument>("hangfire.schema");
        await schema.DeleteManyAsync(new BsonDocument());
        await schema.InsertOneAsync(new BsonDocument("_id", 2));

        var ex = Assert.Throws<HangfireSchemaException>(() => InitializeStorage(database, name));

        await Assert.That(ex.Message).Contains(name);
        await Assert.That(ex.Message).Contains("unknown schema marker '2'");
        await Assert.That(RecurringJobIdsRaw(database)).IsEqualTo(RecurringJobCount);
    }

    [Test]
    public async Task SecondMigrationFromSameSchema_DoesNotFailOnExistingBackup()
    {
        var (database, name) = NewDatabase();
        SeedRecurringJobs(name);
        var schema = database.GetCollection<BsonDocument>("hangfire.schema");
        for (var round = 0; round < 2; round++)
        {
            await schema.DeleteManyAsync(new BsonDocument());
            InitializeStorage(database, name);
            // backup postfix carries a timestamp with second resolution
            await Task.Delay(1100);
        }

        await Assert.That(RecurringJobIds(name)).IsEquivalentTo(ExpectedJobIds());
    }

    private static (IMongoDatabase Database, string Name) NewDatabase()
    {
        var name = $"hangfire-tests-{Guid.NewGuid():N}";
        return (_client!.GetDatabase(name), name);
    }

    private static string[] ExpectedJobIds() => Enumerable.Range(0, RecurringJobCount).Select(i => $"tenant-{i}-trigger").ToArray();

    private static void InitializeStorage(IMongoDatabase database, string name)
    {
        var options = HangfireMongoStorageSetup.CreateStorageOptions();
        HangfireMongoStorageSetup.PrepareSchema(database, options);
        _ = new MongoStorage(_client!, name, options);
    }

    private static MongoStorage CreateStorage(string name) =>
        new(_client!, name, HangfireMongoStorageSetup.CreateStorageOptions());

    private static void SeedRecurringJobs(string name)
    {
        var storage = CreateStorage(name);
        var manager = new RecurringJobManager(storage);
        for (var i = 0; i < RecurringJobCount; i++)
        {
            manager.AddOrUpdate($"tenant-{i}-trigger", () => Console.WriteLine("tick"), "0 * * * *");
        }
    }

    private static string[] RecurringJobIds(string name)
    {
        using var connection = CreateStorage(name).GetConnection();
        return connection.GetRecurringJobs().Select(j => j.Id).ToArray();
    }

    private static long RecurringJobIdsRaw(IMongoDatabase database) =>
        database.GetCollection<BsonDocument>("hangfire.jobGraph")
            .CountDocuments(Builders<BsonDocument>.Filter.Regex("Key", new BsonRegularExpression("^recurring-job:")));

    private static async Task<int> SchemaVersionAsync(IMongoDatabase database)
    {
        var doc = await database.GetCollection<BsonDocument>("hangfire.schema").Find(new BsonDocument()).SingleAsync();
        return doc["_id"].AsInt32;
    }

    private static async Task<List<string>> BackupCollectionNamesAsync(IMongoDatabase database)
    {
        var names = await (await database.ListCollectionNamesAsync()).ToListAsync();
        return names.Where(n => n.Contains("migrationbackup")).ToList();
    }
}
