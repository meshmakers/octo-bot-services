using System.Security.Cryptography;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Backend.Jobs.Tests.Secrets;
using Meshmakers.Octo.Backend.Jobs.Tests.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Services.ArtifactStorage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs;

/// <summary>
///     Shared set-up of the artifact store job tests (AB#5559 / AB#5561): a real file system store with generated
///     keys, a system context whose mongodump writes a random dump and whose mongorestore is captured.
/// </summary>
public abstract class ArtifactStoreJobTestBase : IDisposable
{
    protected const string Tenant = "tenant-1";
    protected readonly ISecretSweepCoordinator _coordinator = Substitute.For<ISecretSweepCoordinator>();
    private protected readonly ArtifactTestEnvironment _env = new("k1");
    protected readonly IBackupFileStorageService _files = Substitute.For<IBackupFileStorageService>();
    private protected readonly InMemorySecretSweepRunStore _runs = new();
    protected readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();
    protected readonly byte[] _dump = RandomNumberGenerator.GetBytes(64 * 1024 + 3);

    protected ArtifactStoreJobTestBase()
    {
        _systemContext.IsSystemTenantExistingAsync().Returns(true);
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.DatabaseName.Returns("tenant1db");
        _systemContext.FindTenantContextAsync(Tenant).Returns(tenantContext);
        _systemContext.BackupTenantAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken?>())
            .Returns(ci =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ci.ArgAt<string>(1))!);
                File.WriteAllBytes(ci.ArgAt<string>(1), _dump);
                return Task.FromResult(new CommandResult { Success = true });
            });
        _files.DeleteFileAsync(Arg.Any<string>()).Returns(ci =>
        {
            File.Delete(ci.Arg<string>());
            return Task.CompletedTask;
        });
        _coordinator.SweepTenantAsync(Arg.Any<string>(), Arg.Any<SecretSweepMode>(), Arg.Any<SecretSweepTrigger>(),
                Arg.Any<SecretSweepRunInfo?>(), Arg.Any<CancellationToken>())
            .Returns(ci => new SecretSweepReport
            {
                TenantId = ci.ArgAt<string>(0), Mode = ci.ArgAt<SecretSweepMode>(1),
                Outcome = SecretSweepOutcome.Succeeded, RemainingLegacyValues = 2
            });
    }

    public void Dispose()
    {
        _env.Dispose();
    }

    /// <summary>Captures what mongorestore would have read.</summary>
    protected List<(string Database, string? Source, byte[] Content)> CaptureRestores()
    {
        var restores = new List<(string, string?, byte[])>();
        _systemContext.RestoreTenantAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken?>())
            .Returns(ci =>
            {
                restores.Add((ci.ArgAt<string>(1), ci.ArgAt<string?>(3), File.ReadAllBytes(ci.ArgAt<string>(2))));
                return Task.FromResult(new CommandResult { Success = true });
            });
        return restores;
    }

    protected SecretSweepCoordinator CreateRealCoordinator()
    {
        var maintenance = Substitute.For<ISecretMaintenanceService>();
        maintenance.SweepTenantAsync(Arg.Any<string>(), Arg.Any<SecretSweepMode>(), Arg.Any<SecretSweepOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => new SecretSweepResult(ci.ArgAt<string>(0), ci.ArgAt<SecretSweepMode>(1))
                { CompletedAt = DateTime.UtcNow });
        var protector = Substitute.For<ISecretAttributeProtector>();
        protector.IsConfigured.Returns(true);
        protector.ActiveKeyId.Returns("k1");
        return new SecretSweepCoordinator(Substitute.For<ILogger<SecretSweepCoordinator>>(), _systemContext,
            maintenance, protector, _env.Storage, Substitute.For<ISecretSweepReportStore>(),
            Options.Create(new SecretSweepJobOptions()), null, null, _runs);
    }
}
