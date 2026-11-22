using Hangfire;
using Hangfire.Mongo;
using Hangfire.Storage;
using Meshmakers.Octo.Backend.BotServices;
using Meshmakers.Octo.Backend.BotServices.HangfireSetup;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace Meshmakers.Octo.Backend.Jobs.IntegrationTests;

/// <summary>
///     AB#9640 — the Hangfire job storage logs in against a MongoDB that allows SCRAM-SHA-256 only (like the clusters),
///     with a user that has SCRAM-SHA-256 credentials only. test-2 refused the start of bot-services after MongoDB.Driver
///     3.14.1: the Hangfire client was built from <c>MongoUrlBuilder.ToString()</c>, which the driver redacts since
///     3.14.1, so it logged in as the user <c>&lt;redacted&gt;</c>, the server found no mechanisms for that user and the
///     driver fell back to SCRAM-SHA-1 ("Received authentication for mechanism SCRAM-SHA-1 which is not enabled").
/// </summary>
[NotInParallel]
public class HangfireMongoAuthenticationTests
{
    private const string RootUser = "octo-root";
    private const string RootPassword = "OctoRoot1";
    private const string AdminUser = "octo-system-admin";
    private const string AdminPassword = "OctoAdmin1";

    private static MongoDbContainer? _container;

    [Before(Class)]
    public static async Task StartMongoAsync()
    {
        _container = new MongoDbBuilder("mongo:8.0.15")
            .WithName($"mongodb-bot-hangfire-auth-tests-{Guid.NewGuid():N}")
            .WithUsername(RootUser)
            .WithPassword(RootPassword)
            .WithCommand("--setParameter", "authenticationMechanisms=SCRAM-SHA-256")
            .Build();
        using var startCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await _container.StartAsync(startCts.Token);

        // The service's admin user, with SCRAM-SHA-256 credentials only.
        using var root = new MongoClient(_container.GetConnectionString());
        await root.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "createUser", AdminUser },
            { "pwd", AdminPassword },
            { "roles", new BsonArray { new BsonDocument { { "role", "root" }, { "db", "admin" } } } },
            { "mechanisms", new BsonArray { "SCRAM-SHA-256" } }
        });
    }

    [After(Class)]
    public static async Task StopMongoAsync()
    {
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }

    [Test]
    public async Task HangfireConnection_LogsInWithScramSha256_PreparesSchemaAndStoresJobs()
    {
        var botOptions = new OctoBotServicesOptions { JobDatabaseName = $"jobs{Guid.NewGuid():N}"[..20] };
        var url = HangfireMongoConnection.CreateUrl(SystemConfiguration(), botOptions);

        // The same calls as Program.cs (AddHangfire): one client from the MongoUrl, the schema pre-flight, the storage.
        using var client = new MongoClient(url);
        var options = HangfireMongoStorageSetup.CreateStorageOptions();
        HangfireMongoStorageSetup.PrepareSchema(client.GetDatabase(url.DatabaseName), options);
        var storage = new MongoStorage(client, url.DatabaseName, options);

        new RecurringJobManager(storage).AddOrUpdate("ab9640-probe", () => Console.WriteLine("probe"), Cron.Daily);
        using var connection = storage.GetConnection();
        var recurringJobs = connection.GetRecurringJobs();

        await Assert.That(recurringJobs.Select(j => j.Id)).Contains("ab9640-probe");
    }

    [Test]
    public async Task RedactedUrlString_IsRefusedByTheServer()
    {
        // The broken code path before the fix: a client built from the string form of the URL builder. Only drivers that
        // redact ToString() (3.14.1 and later, CSHARP-6205) break it, so the check runs only there.
        var builder = new MongoUrlBuilder(HangfireMongoConnection.CreateUrl(SystemConfiguration(), new OctoBotServicesOptions()).Url);
        var urlString = builder.ToString();
        Skip.Unless(urlString.Contains("<redacted>"), "This MongoDB.Driver does not redact MongoUrlBuilder.ToString().");

        using var client = new MongoClient(urlString);
        var exception = await Assert.That(async () =>
            {
                await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
            })
            .Throws<MongoAuthenticationException>();

        await Assert.That(exception!.InnerException?.Message ?? string.Empty).Contains("SCRAM-SHA-1");
    }

    private static OctoSystemConfiguration SystemConfiguration() => new()
    {
        DatabaseHost = $"{_container!.Hostname}:{_container.GetMappedPublicPort(MongoDbBuilder.MongoDbPort)}",
        AdminUser = AdminUser,
        AdminUserPassword = AdminPassword,
        AuthenticationDatabaseName = "admin",
        UseDirectConnection = true
    };
}
