using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Services.ArtifactStorage;
using Meshmakers.Octo.Services.ArtifactStorage.Configuration;
using Meshmakers.Octo.Services.ArtifactStorage.Staging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Services;

/// <summary>
///     AB#6969 — the start-up line about the artifact store names provider, bucket or container, the CrateDB endpoint (yes/no)
///     and whether copy staging is possible; no key, secret, SAS or URI with credentials ever appears in it.
/// </summary>
public class ArtifactStoreStartupLogTests
{
    private const string AccessKey = "AKIACANARYACCESSKEY";
    private const string SecretKey = "S3cr3t/CANARY+secretkey";
    private const string AccountKey = "AZ-CANARY-ACCOUNT-KEY==";
    private const string ConnectionString = "DefaultEndpointsProtocol=https;AccountName=canary;AccountKey=AZ-CANARY-CONNSTR==";
    private const string CredentialUri = "https://user:URI-CANARY-PASSWORD@store.example/bucket";

    private static CapturingLogger<ArtifactStoreStartupLogTests> Run(ArtifactStorageOptions options,
        IArchiveCopyStagingLocationFactory? factory)
    {
        var logger = new CapturingLogger<ArtifactStoreStartupLogTests>();
        ArtifactStoreStartupLog.Write(logger, options, factory);
        return logger;
    }

    private static IReadOnlyList<string> Log(ArtifactStorageOptions options, IArchiveCopyStagingLocationFactory? factory)
    {
        return Run(options, factory).Messages;
    }

    private static IArchiveCopyStagingLocationFactory Factory(bool supported, string? reason = null)
    {
        var factory = Substitute.For<IArchiveCopyStagingLocationFactory>();
        factory.IsSupported.Returns(supported);
        factory.UnsupportedReason.Returns(reason);
        return factory;
    }

    [Test]
    public async Task S3_NamesTheBucket_TheCrateEndpointAnswer_AndStagingYes()
    {
        var options = new ArtifactStorageOptions
        {
            Provider = ArtifactStorageProvider.S3,
            InstancePrefix = "local",
            S3 = { Bucket = "octo-artifacts", ServiceUrl = CredentialUri, CrateDbServiceUrl = CredentialUri, AccessKeyId = AccessKey, SecretAccessKey = SecretKey }
        };

        var line = Log(options, Factory(true)).Single();

        await Assert.That(line.Contains("S3")).IsTrue();
        await Assert.That(line.Contains("bucket 'octo-artifacts'")).IsTrue();
        await Assert.That(line.Contains("CrateDB endpoint set: yes")).IsTrue();
        await Assert.That(line.Contains("copy staging possible: yes")).IsTrue();
    }

    [Test]
    public async Task StagingPossible_IsLoggedAsInformation_NotPossible_AsAWarningWithTheReason()
    {
        var options = new ArtifactStorageOptions { Provider = ArtifactStorageProvider.S3, S3 = { Bucket = "b", CrateDbServiceUrl = "http://crate" } };

        await Assert.That(Run(options, Factory(true)).Warnings).IsEqualTo(0);

        var notPossible = Run(options, Factory(false, "S3:CrateDbServiceUrl is not set"));
        await Assert.That(notPossible.Warnings).IsEqualTo(1);
        await Assert.That(notPossible.Messages.Single().Contains("S3:CrateDbServiceUrl is not set")).IsTrue();

        await Assert.That(Run(options, null).Warnings).IsEqualTo(1);
    }

    [Test]
    public async Task S3WithoutCrateEndpoint_SaysNo()
    {
        var options = new ArtifactStorageOptions { Provider = ArtifactStorageProvider.S3, S3 = { Bucket = "b" } };

        var line = Log(options, Factory(false, "S3:CrateDbServiceUrl is not set")).Single();

        await Assert.That(line.Contains("CrateDB endpoint set: no")).IsTrue();
        await Assert.That(line.Contains("copy staging possible: no (S3:CrateDbServiceUrl is not set)")).IsTrue();
    }

    [Test]
    public async Task FileSystem_SaysStagingNo_WithTheReason()
    {
        var line = Log(new ArtifactStorageOptions { Provider = ArtifactStorageProvider.FileSystem },
            Factory(false, "the file system store has no staging area")).Single();

        await Assert.That(line.Contains("FileSystem")).IsTrue();
        await Assert.That(line.Contains("copy staging possible: no (the file system store has no staging area)")).IsTrue();
    }

    [Test]
    public async Task AzureBlob_NamesTheContainer()
    {
        var options = new ArtifactStorageOptions
        {
            Provider = ArtifactStorageProvider.AzureBlob,
            AzureBlob = { Container = "octo-artifacts", AccountKey = AccountKey, ConnectionString = ConnectionString, AccountUrl = CredentialUri }
        };

        var line = Log(options, Factory(true)).Single();

        await Assert.That(line.Contains("container 'octo-artifacts'")).IsTrue();
    }

    [Test]
    public async Task NoStagingFactory_SaysNo()
    {
        var line = Log(new ArtifactStorageOptions(), null).Single();

        await Assert.That(line.Contains("copy staging possible: no (no staging location factory is registered)")).IsTrue();
    }

    [Test]
    [Arguments(ArtifactStorageProvider.S3)]
    [Arguments(ArtifactStorageProvider.AzureBlob)]
    [Arguments(ArtifactStorageProvider.FileSystem)]
    public async Task NoSecretEverAppearsInTheLine(ArtifactStorageProvider provider)
    {
        var options = new ArtifactStorageOptions
        {
            Provider = provider,
            S3 = { Bucket = "b", ServiceUrl = CredentialUri, CrateDbServiceUrl = CredentialUri, AccessKeyId = AccessKey, SecretAccessKey = SecretKey },
            AzureBlob = { Container = "c", AccountUrl = CredentialUri, AccountKey = AccountKey, ConnectionString = ConnectionString },
            FileSystem = { RootPath = "/var/octo" }
        };

        foreach (var line in Log(options, Factory(false, "reason")))
        {
            foreach (var canary in new[] { AccessKey, SecretKey, AccountKey, "AZ-CANARY-CONNSTR", "URI-CANARY-PASSWORD", "store.example", "AccountKey=" })
            {
                await Assert.That(line.Contains(canary)).IsFalse();
            }
        }
    }
}
