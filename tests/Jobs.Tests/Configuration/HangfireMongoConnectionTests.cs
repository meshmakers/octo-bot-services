using Meshmakers.Octo.Backend.BotServices;
using Meshmakers.Octo.Backend.BotServices.HangfireSetup;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Configuration;

/// <summary>
///     AB#9640 — the connection of the Hangfire job storage keeps the configured credentials. Since MongoDB.Driver 3.14.1
///     <c>MongoUrlBuilder.ToString()</c> redacts user name and password; the bot service built its Hangfire client from
///     that string and logged in as the user <c>&lt;redacted&gt;</c> (SCRAM-SHA-1 fallback, start refused on test-2).
/// </summary>
public class HangfireMongoConnectionTests
{
    private const string User = "octo-system-admin";
    private const string Password = "dummy-pass:word@/?";

    private static OctoSystemConfiguration SystemConfig(string host = "mongo-0.example:27017") => new()
    {
        DatabaseHost = host,
        AdminUser = User,
        AdminUserPassword = Password,
        AuthenticationDatabaseName = "authdb"
    };

    [Test]
    public async Task CreateUrl_KeepsUserPasswordAndAuthenticationSource()
    {
        var url = HangfireMongoConnection.CreateUrl(SystemConfig(), new OctoBotServicesOptions());

        await Assert.That(url.Username).IsEqualTo(User);
        await Assert.That(url.Password).IsEqualTo(Password);
        await Assert.That(url.AuthenticationSource).IsEqualTo("authdb");
        await Assert.That(url.AuthenticationMechanism).IsNull();
    }

    [Test]
    public async Task CreateUrl_ClientSettingsCarryTheConfiguredCredential()
    {
        var url = HangfireMongoConnection.CreateUrl(SystemConfig(), new OctoBotServicesOptions());

        var credential = MongoClientSettings.FromUrl(url).Credential;

        await Assert.That(credential).IsNotNull();
        await Assert.That(credential!.Username).IsEqualTo(User);
        await Assert.That(credential.Source).IsEqualTo("authdb");
        // No mechanism is forced: the driver negotiates it with the server (SCRAM-SHA-256 where it is the only one).
        await Assert.That(credential.Mechanism).IsNull();
    }

    [Test]
    public async Task CreateUrl_UnredactedUrlRoundTripsTheCredentials()
    {
        // MongoUrl.Url is the unredacted form (ToString() is redacted since driver 3.14.1, CSHARP-6205).
        var url = HangfireMongoConnection.CreateUrl(SystemConfig(), new OctoBotServicesOptions());

        var reparsed = new MongoUrl(url.Url);

        await Assert.That(reparsed.Username).IsEqualTo(User);
        await Assert.That(reparsed.Password).IsEqualTo(Password);
        await Assert.That(reparsed.AuthenticationSource).IsEqualTo("authdb");
    }

    [Test]
    public async Task CreateUrl_DatabaseName_DefaultAndInstancePrefix()
    {
        var plain = HangfireMongoConnection.CreateUrl(SystemConfig(), new OctoBotServicesOptions { JobDatabaseName = "OctoSystemJobs" });
        var prefixed = HangfireMongoConnection.CreateUrl(SystemConfig(),
            new OctoBotServicesOptions { JobDatabaseName = "OctoSystemJobs", InstancePrefix = "dev" });

        await Assert.That(plain.DatabaseName).IsEqualTo("OctoSystemJobs");
        await Assert.That(prefixed.DatabaseName).IsEqualTo("dev-OctoSystemJobs");
    }

    [Test]
    public async Task CreateUrl_HostWithPortAndHostList()
    {
        var single = HangfireMongoConnection.CreateUrl(SystemConfig("mongo-0.example:27018"), new OctoBotServicesOptions());
        var list = HangfireMongoConnection.CreateUrl(SystemConfig("mongo-0.example:27017,mongo-1.example:27017"),
            new OctoBotServicesOptions());

        await Assert.That(single.Server.Host).IsEqualTo("mongo-0.example");
        await Assert.That(single.Server.Port).IsEqualTo(27018);
        await Assert.That(list.Servers.Select(s => s.Host)).IsEquivalentTo(["mongo-0.example", "mongo-1.example"]);
    }

    [Test]
    public async Task CreateUrl_PassesTlsAndConnectionFlags()
    {
        var system = SystemConfig();
        system.UseTls = true;
        system.AllowInsecureTls = true;
        system.UseDirectConnection = true;

        var url = HangfireMongoConnection.CreateUrl(system, new OctoBotServicesOptions());

        await Assert.That(url.UseTls).IsTrue();
        await Assert.That(url.AllowInsecureTls).IsTrue();
        await Assert.That(url.DirectConnection).IsTrue();
    }
}
