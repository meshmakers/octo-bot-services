using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.Jobs.IndexUpgrade;

/// <summary>
///     The MongoDB client of the index upgrade (AB#9118): reads the index lists of the tenant databases and writes the
///     last-run records into the system database. One admin client per process, built from
///     <see cref="OctoSystemConfiguration" /> like the method operation store of octo-common-services; it never runs a
///     transaction and never writes to a tenant database (the engine builds the indexes).
/// </summary>
public interface IIndexUpgradeMongoClient
{
    /// <summary>A tenant database by name.</summary>
    IMongoDatabase GetDatabase(string databaseName);

    /// <summary>The system database (<see cref="OctoSystemConfiguration.SystemDatabaseName" />).</summary>
    IMongoDatabase GetSystemDatabase();
}

/// <inheritdoc />
internal sealed class IndexUpgradeMongoClient : IIndexUpgradeMongoClient
{
    private readonly Lazy<IMongoClient> _client;
    private readonly string _systemDatabaseName;

    public IndexUpgradeMongoClient(IOptions<OctoSystemConfiguration> systemConfiguration)
    {
        var configuration = systemConfiguration.Value;
        _systemDatabaseName = configuration.SystemDatabaseName.Trim().ToLowerInvariant();
        _client = new Lazy<IMongoClient>(() => new MongoClient(CreateUrl(configuration)));
    }

    /// <summary>For tests: a given client.</summary>
    internal IndexUpgradeMongoClient(IMongoClient client, string systemDatabaseName)
    {
        _client = new Lazy<IMongoClient>(client);
        _systemDatabaseName = systemDatabaseName.Trim().ToLowerInvariant();
    }

    public IMongoDatabase GetDatabase(string databaseName) => _client.Value.GetDatabase(databaseName);

    public IMongoDatabase GetSystemDatabase() => _client.Value.GetDatabase(_systemDatabaseName);

    internal static MongoUrl CreateUrl(OctoSystemConfiguration configuration)
    {
        var builder = new MongoUrlBuilder
        {
            Username = configuration.AdminUser,
            Password = configuration.AdminUserPassword,
            AuthenticationSource = configuration.AuthenticationDatabaseName,
            ApplicationName = "octo-bot-index-upgrade",
            UseTls = configuration.UseTls,
            AllowInsecureTls = configuration.AllowInsecureTls,
            DirectConnection = configuration.UseDirectConnection,
            RetryReads = true,
            RetryWrites = true
        };

        // Parse, not the string ctor: DatabaseHost may be "host:port" (MongoDB.Driver 3.11.1 host validation).
        if (configuration.DatabaseHost.Contains(','))
        {
            builder.Servers = configuration.DatabaseHost.Split(',').Select(MongoServerAddress.Parse);
        }
        else
        {
            builder.Server = MongoServerAddress.Parse(configuration.DatabaseHost);
        }

        if (!string.IsNullOrWhiteSpace(configuration.ReplicaSetName))
        {
            builder.ReplicaSetName = configuration.ReplicaSetName;
        }

        return builder.ToMongoUrl();
    }
}
