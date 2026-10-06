using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Secrets;

/// <summary>
///     AB#5539 — bot-side orchestration of the secret sweep, with a mocked <see cref="ISecretMaintenanceService" />.
/// </summary>
public class SecretSweepCoordinatorTests : IDisposable
{
    private readonly IBackupFileStorageService _storage = Substitute.For<IBackupFileStorageService>();
    private readonly ISecretMaintenanceService _maintenance = Substitute.For<ISecretMaintenanceService>();
    private readonly SecretSweepJobOptions _options = new();
    private readonly ISecretAttributeProtector _protector = Substitute.For<ISecretAttributeProtector>();
    private readonly ISecretSweepReportStore _reportStore = Substitute.For<ISecretSweepReportStore>();
    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"octo-secret-sweep-{Guid.NewGuid():N}");
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));

    public SecretSweepCoordinatorTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _protector.IsConfigured.Returns(true);
        _protector.ActiveKeyId.Returns("k1");
        _storage.CreateSecretBackupFilePath(Arg.Any<string>())
            .Returns(ci => Path.Combine(_tempDirectory, $"{ci.Arg<string>()}.presweep.tar.gz"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    private SecretSweepCoordinator CreateCoordinator()
    {
        return new SecretSweepCoordinator(Substitute.For<ILogger<SecretSweepCoordinator>>(), _systemContext,
            _maintenance, _protector, _storage, _reportStore, Options.Create(_options), _time);
    }

    private void SetupSweep(string tenantId, SecretSweepMode mode, Action<SecretSweepResult>? configure = null)
    {
        _maintenance.SweepTenantAsync(tenantId, mode, Arg.Any<SecretSweepOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var result = new SecretSweepResult(tenantId, mode) { CompletedAt = DateTime.UtcNow };
                configure?.Invoke(result);
                return Task.FromResult(result);
            });
    }

    private void SetupBackupSucceeds()
    {
        _systemContext.BackupTenantAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken?>())
            .Returns(ci =>
            {
                File.WriteAllText(ci.ArgAt<string>(1), "dump");
                return Task.FromResult(new CommandResult { Success = true });
            });
    }

    private static void AddPlaintext(SecretSweepResult result, int count)
    {
        for (var i = 0; i < count; i++)
        {
            result.Totals.Add(SecretValueForm.Plaintext);
        }
    }

    [Test]
    public async Task Verify_RunsOneStep_WithoutBackup_AndStoresTheReport()
    {
        SetupSweep("t-verify", SecretSweepMode.Verify, r => AddPlaintext(r, 2));

        var report = await CreateCoordinator()
            .SweepTenantAsync("t-verify", SecretSweepMode.Verify, SecretSweepTrigger.Recurring, CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Succeeded);
        await Assert.That(report.Steps.Count).IsEqualTo(1);
        await Assert.That(report.Steps[0].Mode).IsEqualTo(SecretSweepMode.Verify);
        await Assert.That(report.RemainingLegacyValues).IsEqualTo(2);
        await Assert.That(report.BackupFileName).IsNull();
        await Assert.That(report.Trigger).IsEqualTo(SecretSweepTrigger.Recurring);
        await _systemContext.DidNotReceiveWithAnyArgs().BackupTenantAsync(default!, default!);
        await _reportStore.Received(1).SaveAsync(report);
    }

    [Test]
    public async Task Verify_WorksWithoutKeys()
    {
        _protector.IsConfigured.Returns(false);
        SetupSweep("t-verify-nokeys", SecretSweepMode.Verify);

        var report = await CreateCoordinator().SweepTenantAsync("t-verify-nokeys", SecretSweepMode.Verify,
            SecretSweepTrigger.Manual, CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Succeeded);
        await Assert.That(report.ActiveKeyId).IsNull();
    }

    [Test]
    public async Task Encrypt_WithoutKeys_IsSkipped_AndTouchesNothing()
    {
        _protector.IsConfigured.Returns(false);

        var report = await CreateCoordinator().SweepTenantAsync("t-nokeys", SecretSweepMode.Encrypt,
            SecretSweepTrigger.Manual, CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Skipped);
        await Assert.That(report.Reason).Contains("not configured");
        await _maintenance.DidNotReceiveWithAnyArgs()
            .SweepTenantAsync(default!, default, default(SecretSweepOptions)!, default);
        await _systemContext.DidNotReceiveWithAnyArgs().BackupTenantAsync(default!, default!);
        await _reportStore.Received(1).SaveAsync(report);
    }

    [Test]
    public async Task Encrypt_TakesBackupFirst_ThenEncrypts_ThenVerifies()
    {
        SetupBackupSucceeds();
        SetupSweep("t-enc", SecretSweepMode.Encrypt, r =>
        {
            AddPlaintext(r, 3);
            r.ValuesRewritten = 3;
        });
        SetupSweep("t-enc", SecretSweepMode.Verify, r => r.Totals.Add(SecretValueForm.EncV2, "k1"));

        var report = await CreateCoordinator().SweepTenantAsync("t-enc", SecretSweepMode.Encrypt,
            SecretSweepTrigger.Manual, CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Succeeded);
        await Assert.That(report.Steps.Select(s => s.Mode).ToArray())
            .IsEquivalentTo(new[] { SecretSweepMode.Encrypt, SecretSweepMode.Verify });
        await Assert.That(report.Steps[0].ValuesRewritten).IsEqualTo(3);
        await Assert.That(report.Steps[1].Totals.EncV2ByKeyId["k1"]).IsEqualTo(1);
        await Assert.That(report.RemainingLegacyValues).IsEqualTo(0);
        await Assert.That(report.BackupFileName).IsEqualTo("t-enc.presweep.tar.gz");
        await Assert.That(report.ActiveKeyId).IsEqualTo("k1");
        _storage.Received(1).RestrictToOwner(Path.Combine(_tempDirectory, "t-enc.presweep.tar.gz"));

        Received.InOrder(() =>
        {
            _systemContext.BackupTenantAsync("t-enc", Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken?>());
            _maintenance.SweepTenantAsync("t-enc", SecretSweepMode.Encrypt, Arg.Any<SecretSweepOptions>(),
                Arg.Any<CancellationToken>());
            _maintenance.SweepTenantAsync("t-enc", SecretSweepMode.Verify, Arg.Any<SecretSweepOptions>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task Encrypt_UsesTheConfiguredBatchSize()
    {
        _options.BatchSize = 42;
        SetupBackupSucceeds();
        SetupSweep("t-batch", SecretSweepMode.Encrypt);
        SetupSweep("t-batch", SecretSweepMode.Verify);

        await CreateCoordinator().SweepTenantAsync("t-batch", SecretSweepMode.Encrypt, SecretSweepTrigger.Manual,
            CancellationToken.None);

        await _maintenance.Received(1).SweepTenantAsync("t-batch", SecretSweepMode.Encrypt,
            Arg.Is<SecretSweepOptions>(o => o.BatchSize == 42 && !o.ConfirmDecrypt && o.CkModelName == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Encrypt_BackupFails_IsSkippedFailSafe_AndDeletesThePartialFile()
    {
        _systemContext.BackupTenantAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken?>())
            .Returns(new CommandResult { Success = false, ExitCode = 127 });

        var report = await CreateCoordinator().SweepTenantAsync("t-nodump", SecretSweepMode.Encrypt,
            SecretSweepTrigger.Manual, CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Skipped);
        await Assert.That(report.Reason).Contains("exit code 127");
        await Assert.That(report.Steps).IsEmpty();
        await _maintenance.DidNotReceiveWithAnyArgs()
            .SweepTenantAsync(default!, default, default(SecretSweepOptions)!, default);
        await _storage.Received(1).DeleteFileAsync(Path.Combine(_tempDirectory, "t-nodump.presweep.tar.gz"));
    }

    [Test]
    public async Task Encrypt_BackupThrows_NoDumpInfrastructure_IsSkipped()
    {
        _systemContext.BackupTenantAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken?>())
            .ThrowsAsync(new System.ComponentModel.Win32Exception("mongodump not found"));

        var report = await CreateCoordinator().SweepTenantAsync("t-notool", SecretSweepMode.Reprotect,
            SecretSweepTrigger.Manual, CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Skipped);
        await Assert.That(report.Reason).Contains("Win32Exception");
        await _maintenance.DidNotReceiveWithAnyArgs()
            .SweepTenantAsync(default!, default, default(SecretSweepOptions)!, default);
    }

    [Test]
    public async Task Encrypt_BackupDirectoryUnusable_IsSkipped()
    {
        _storage.CreateSecretBackupFilePath("t-baddir").Throws(new UnauthorizedAccessException("denied"));

        var report = await CreateCoordinator().SweepTenantAsync("t-baddir", SecretSweepMode.Encrypt,
            SecretSweepTrigger.Manual, CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Skipped);
        await Assert.That(report.Reason).Contains("backup directory not usable");
    }

    [Test]
    public async Task Encrypt_BackupFails_ButNotRequired_RunsAndSaysSo()
    {
        _options.RequirePreSweepBackup = false;
        _systemContext.BackupTenantAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken?>())
            .Returns(new CommandResult { Success = false, ExitCode = 1 });
        SetupSweep("t-optional", SecretSweepMode.Encrypt);
        SetupSweep("t-optional", SecretSweepMode.Verify);

        var report = await CreateCoordinator().SweepTenantAsync("t-optional", SecretSweepMode.Encrypt,
            SecretSweepTrigger.Manual, CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Succeeded);
        await Assert.That(report.Reason).Contains("not required");
        await Assert.That(report.BackupFileName).IsNull();
        await Assert.That(report.Steps.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Decrypt_IsRefused()
    {
        var report = await CreateCoordinator().SweepTenantAsync("t-decrypt", SecretSweepMode.Decrypt,
            SecretSweepTrigger.Manual, CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Skipped);
        await _maintenance.DidNotReceiveWithAnyArgs()
            .SweepTenantAsync(default!, default, default(SecretSweepOptions)!, default);
        await _systemContext.DidNotReceiveWithAnyArgs().BackupTenantAsync(default!, default!);
    }

    [Test]
    public async Task SweepThrows_IsReportedAsFailed_NotThrown()
    {
        _maintenance.SweepTenantAsync("t-throws", SecretSweepMode.Verify, Arg.Any<SecretSweepOptions>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new SecretEncryptionNotConfiguredException("no active key"));

        var report = await CreateCoordinator().SweepTenantAsync("t-throws", SecretSweepMode.Verify,
            SecretSweepTrigger.Manual, CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Failed);
        await Assert.That(report.Reason).Contains(nameof(SecretEncryptionNotConfiguredException));
        await _reportStore.Received(1).SaveAsync(report);
    }

    [Test]
    public async Task ValueFailures_AreReportedAsCompletedWithFailures()
    {
        var rtId = new OctoObjectId("6512a1b2c3d4e5f601020304");
        SetupSweep("t-fail", SecretSweepMode.Verify, r =>
        {
            r.Totals.Add(SecretValueForm.EncV2, "k1");
            r.Totals.AddFailure();
            r.Failures.Add(new SecretSweepFailure("Test/Type", rtId, "Password", "CryptographicException: tag mismatch"));
        });

        var report = await CreateCoordinator().SweepTenantAsync("t-fail", SecretSweepMode.Verify,
            SecretSweepTrigger.Manual, CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.CompletedWithFailures);
        await Assert.That(report.Steps[0].Failures[0].RtId).IsEqualTo("6512a1b2c3d4e5f601020304");
        await Assert.That(report.Steps[0].Totals.Failed).IsEqualTo(1);
    }

    [Test]
    public async Task ReportStoreFailure_DoesNotFailTheSweep()
    {
        SetupSweep("t-store", SecretSweepMode.Verify);
        _reportStore.SaveAsync(Arg.Any<SecretSweepReport>()).ThrowsAsync(new InvalidOperationException("storage down"));

        var report = await CreateCoordinator().SweepTenantAsync("t-store", SecretSweepMode.Verify,
            SecretSweepTrigger.Manual, CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Succeeded);
    }

    [Test]
    public async Task StrictMode_PlaintextRemaining_IsAViolation_AndFeedsTheGauge()
    {
        _options.StrictModeSince = _time.GetUtcNow().AddDays(-1);
        SetupSweep("t-strict-bad", SecretSweepMode.Verify, r =>
        {
            AddPlaintext(r, 2);
            r.Totals.Add(SecretValueForm.EncV1);
        });

        var report = await CreateCoordinator().SweepTenantAsync("t-strict-bad", SecretSweepMode.Verify,
            SecretSweepTrigger.Recurring, CancellationToken.None);

        await Assert.That(report.StrictModeActive).IsTrue();
        await Assert.That(report.StrictModeViolation).IsTrue();
        await Assert.That(report.RemainingLegacyValues).IsEqualTo(3);
        await Assert.That(SecretSweepStrictModeDiagnostics.Get("t-strict-bad")).IsEqualTo(3);
    }

    [Test]
    public async Task StrictMode_NoLegacyValues_IsCompliant_AndReportsZero()
    {
        _options.StrictModeSince = _time.GetUtcNow().AddDays(-1);
        SetupSweep("t-strict-ok", SecretSweepMode.Verify, r => r.Totals.Add(SecretValueForm.EncV2, "k1"));

        var report = await CreateCoordinator().SweepTenantAsync("t-strict-ok", SecretSweepMode.Verify,
            SecretSweepTrigger.Recurring, CancellationToken.None);

        await Assert.That(report.StrictModeViolation).IsFalse();
        await Assert.That(SecretSweepStrictModeDiagnostics.Get("t-strict-ok")).IsEqualTo(0);
    }

    [Test]
    public async Task StrictMode_NotYetInForce_IsNoViolation_AndNotInTheGauge()
    {
        _options.StrictModeSince = _time.GetUtcNow().AddDays(1);
        SecretSweepStrictModeDiagnostics.Record("t-strict-future", 5);
        SetupSweep("t-strict-future", SecretSweepMode.Verify, r => AddPlaintext(r, 1));

        var report = await CreateCoordinator().SweepTenantAsync("t-strict-future", SecretSweepMode.Verify,
            SecretSweepTrigger.Recurring, CancellationToken.None);

        await Assert.That(report.StrictModeActive).IsFalse();
        await Assert.That(report.StrictModeViolation).IsFalse();
        await Assert.That(SecretSweepStrictModeDiagnostics.Get("t-strict-future")).IsNull();
    }

    [Test]
    public async Task StrictMode_EvaluatesTheStateAfterAnEncrypt()
    {
        _options.StrictModeSince = _time.GetUtcNow().AddDays(-1);
        SetupBackupSucceeds();
        SetupSweep("t-strict-enc", SecretSweepMode.Encrypt, r => AddPlaintext(r, 4));
        SetupSweep("t-strict-enc", SecretSweepMode.Verify, r => r.Totals.Add(SecretValueForm.EncV2, "k1"));

        var report = await CreateCoordinator().SweepTenantAsync("t-strict-enc", SecretSweepMode.Encrypt,
            SecretSweepTrigger.Manual, CancellationToken.None);

        await Assert.That(report.StrictModeViolation).IsFalse();
        await Assert.That(report.RemainingLegacyValues).IsEqualTo(0);
    }

    [Test]
    public async Task AfterRestore_ClearsUnknownKids_Encrypts_Verifies_AndListsSecretsToReEnter()
    {
        var rtId = new OctoObjectId("6512a1b2c3d4e5f601020304");
        SetupSweep("t-restore", SecretSweepMode.ClearUnknownKid, r =>
        {
            r.Totals.Add(SecretValueForm.UnknownKeyId, "k7");
            r.Cleared.Add(new SecretSweepClearedValue("System.Communication/SftpConfiguration", rtId, "Password",
                SecretValueForm.UnknownKeyId, "k7"));
            r.ValuesRewritten = 1;
        });
        SetupSweep("t-restore", SecretSweepMode.Encrypt, r => AddPlaintext(r, 1));
        SetupSweep("t-restore", SecretSweepMode.Verify, r => r.Totals.Add(SecretValueForm.EncV2, "k1"));

        var report = await CreateCoordinator().RunAfterRestoreAsync("t-restore", CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Succeeded);
        await Assert.That(report.Trigger).IsEqualTo(SecretSweepTrigger.Restore);
        await Assert.That(report.Steps.Select(s => s.Mode).ToArray()).IsEquivalentTo(new[]
            { SecretSweepMode.ClearUnknownKid, SecretSweepMode.Encrypt, SecretSweepMode.Verify });
        await Assert.That(report.SecretsToReEnter.Count).IsEqualTo(1);
        var secret = report.SecretsToReEnter[0];
        await Assert.That(secret.CkTypeId).IsEqualTo("System.Communication/SftpConfiguration");
        await Assert.That(secret.RtId).IsEqualTo("6512a1b2c3d4e5f601020304");
        await Assert.That(secret.AttributePath).IsEqualTo("Password");
        await Assert.That(secret.KeyId).IsEqualTo("k7");
        // The uploaded backup is the pre-sweep state; no extra dump.
        await _systemContext.DidNotReceiveWithAnyArgs().BackupTenantAsync(default!, default!);
        await _reportStore.Received(1).SaveAsync(report);
    }

    [Test]
    public async Task AfterRestore_WithoutKeys_IsSkipped()
    {
        _protector.IsConfigured.Returns(false);

        var report = await CreateCoordinator().RunAfterRestoreAsync("t-restore-nokeys", CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Skipped);
        await _maintenance.DidNotReceiveWithAnyArgs()
            .SweepTenantAsync(default!, default, default(SecretSweepOptions)!, default);
    }

    [Test]
    public async Task AfterRestore_DisabledByConfiguration_IsSkipped()
    {
        _options.RunAfterRestore = false;

        var report = await CreateCoordinator().RunAfterRestoreAsync("t-restore-off", CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Skipped);
        await Assert.That(report.Reason).Contains("RunAfterRestore");
    }

    [Test]
    public async Task AfterRestore_SweepThrows_IsReportedNotThrown()
    {
        _maintenance.SweepTenantAsync("t-restore-err", SecretSweepMode.ClearUnknownKid,
                Arg.Any<SecretSweepOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var report = await CreateCoordinator().RunAfterRestoreAsync("t-restore-err", CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Failed);
    }

    [Test]
    public async Task GetTenantIds_SystemTenantFirst_ThenAllRegisteredTenants_Deduplicated()
    {
        _systemContext.IsSystemTenantExistingAsync().Returns(true);
        _systemContext.TenantId.Returns("octosystem");
        var resultSet = Substitute.For<IResultSet<OctoTenant>>();
        resultSet.Items.Returns(new[]
        {
            new OctoTenant("parent", "parent"),
            new OctoTenant("child", "child", "parent"),
            new OctoTenant("OctoSystem", "OctoSystem")
        });
        _systemContext.GetAllTenantsAsync(Arg.Any<IOctoAdminSession>(), Arg.Any<int?>(), Arg.Any<int?>())
            .Returns(resultSet);

        var ids = await CreateCoordinator().GetTenantIdsAsync();

        await Assert.That(ids.ToArray()).IsEquivalentTo(new[] { "octosystem", "parent", "child" });
        await Assert.That(ids[0]).IsEqualTo("octosystem");
    }

    [Test]
    public async Task GetTenantIds_NoSystemTenant_IsEmpty()
    {
        _systemContext.IsSystemTenantExistingAsync().Returns(false);

        var ids = await CreateCoordinator().GetTenantIdsAsync();

        await Assert.That(ids).IsEmpty();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
