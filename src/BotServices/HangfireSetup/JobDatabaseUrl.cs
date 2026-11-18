using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.BotServices.HangfireSetup;

/// <summary>The connection of the bot's job database (Hangfire storage, dump registry).</summary>
internal static class JobDatabaseUrl
{
    /// <summary>The URL of the job database, with its database name.</summary>
    public static MongoUrlBuilder Create(OctoBotServicesOptions botOptions, OctoSystemConfiguration systemOptions)
    {
        var databaseName = string.IsNullOrWhiteSpace(botOptions.InstancePrefix)
            ? botOptions.JobDatabaseName
            : $"{botOptions.InstancePrefix}-{botOptions.JobDatabaseName}";
        var mongoUrlBuilder = new MongoUrlBuilder
        {
            DatabaseName = databaseName,
            Username = systemOptions.AdminUser,
            Password = systemOptions.AdminUserPassword,
            AuthenticationSource = systemOptions.AuthenticationDatabaseName,
            UseTls = systemOptions.UseTls,
            DirectConnection = systemOptions.UseDirectConnection,
            AllowInsecureTls = systemOptions.AllowInsecureTls
        };

        // DatabaseHost may carry a port suffix ("host:port"). MongoServerAddress.Parse handles
        // that; the MongoServerAddress(string host) constructor rejects it since MongoDB.Driver
        // 3.11.1 (CSHARP-6171 host validation).
        if (systemOptions.DatabaseHost.Contains(','))
        {
            mongoUrlBuilder.Servers = systemOptions.DatabaseHost.Split(",").Select(MongoServerAddress.Parse);
        }
        else
        {
            mongoUrlBuilder.Server = MongoServerAddress.Parse(systemOptions.DatabaseHost);
        }

        return mongoUrlBuilder;
    }
}
