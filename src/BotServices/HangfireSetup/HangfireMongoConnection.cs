using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.BotServices.HangfireSetup;

/// <summary>
///     The connection of the Hangfire MongoDB job storage (AB#9640).
/// </summary>
/// <remarks>
///     The connection is passed on as a <see cref="MongoUrl" /> (and the client built from it), never as a string: since
///     MongoDB.Driver 3.14.1 (CSHARP-6205) <c>ToString()</c> of <see cref="MongoUrlBuilder" /> and <see cref="MongoUrl" />
///     returns a redacted URL in which user name and password are <c>&lt;redacted&gt;</c>. A client built from that
///     string authenticates as the user <c>&lt;redacted&gt;</c>; the server knows no such user, so the driver falls back
///     to SCRAM-SHA-1 and a server with SCRAM-SHA-256 only refuses the login, and bot-services did not start.
/// </remarks>
internal static class HangfireMongoConnection
{
    /// <summary>
    ///     The URL of the Hangfire job database: the admin credentials and the authentication database of
    ///     <see cref="OctoSystemConfiguration" />, the job database name with the optional instance prefix.
    /// </summary>
    public static MongoUrl CreateUrl(OctoSystemConfiguration systemConfiguration, OctoBotServicesOptions botServicesOptions)
    {
        var builder = new MongoUrlBuilder
        {
            DatabaseName = CreateDatabaseName(botServicesOptions),
            Username = systemConfiguration.AdminUser,
            Password = systemConfiguration.AdminUserPassword,
            AuthenticationSource = systemConfiguration.AuthenticationDatabaseName,
            UseTls = systemConfiguration.UseTls,
            DirectConnection = systemConfiguration.UseDirectConnection,
            AllowInsecureTls = systemConfiguration.AllowInsecureTls
        };

        // DatabaseHost may carry a port suffix ("host:port"). MongoServerAddress.Parse handles
        // that; the MongoServerAddress(string host) constructor rejects it since MongoDB.Driver
        // 3.11.1 (CSHARP-6171 host validation).
        if (systemConfiguration.DatabaseHost.Contains(','))
        {
            builder.Servers = systemConfiguration.DatabaseHost.Split(',').Select(MongoServerAddress.Parse);
        }
        else
        {
            builder.Server = MongoServerAddress.Parse(systemConfiguration.DatabaseHost);
        }

        return builder.ToMongoUrl();
    }

    /// <summary>The job database name, prefixed with the instance prefix when one is set.</summary>
    public static string CreateDatabaseName(OctoBotServicesOptions botServicesOptions)
    {
        return string.IsNullOrWhiteSpace(botServicesOptions.InstancePrefix)
            ? botServicesOptions.JobDatabaseName
            : $"{botServicesOptions.InstancePrefix}-{botServicesOptions.JobDatabaseName}";
    }
}
