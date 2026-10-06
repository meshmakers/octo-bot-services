using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
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

    private SecretSweepCoordinator CreateCoordinator(ISecretSweepTenantLock? tenantLock = null,
        ISecretSweepRunStore? runStore = null)
    {
        return new SecretSweepCoordinator(Substitute.For<ILogger<SecretSweepCoordinator>>(), _systemContext,
            _maintenance, _protector, _storage, _reportStore, Options.Create(_options), _time, tenantLock, runStore);
    }

    /// <summary>
    ///     Verify answers <paramref name="first" /> on its first call and <paramref name="then" /> afterwards.
    /// </summary>
    private void SetupVerifySequence(string tenantId, Action<SecretSweepResult> first, Action<SecretSweepResult> then)
    {
        var calls = 0;
        _maintenance.SweepTenantAsync(tenantId, SecretSweepMode.Verify, Arg.Any<SecretSweepOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var result = new SecretSweepResult(tenantId, SecretSweepMode.Verify) { CompletedAt = DateTime.UtcNow };
                (calls++ == 0 ? first : then)(result);
                return Task.FromResult(result);
            });
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
            .SweepTenantAsync("t-verify", SecretSweepMode.Verify, SecretSweepTrigger.Recurring, null, CancellationToken.None);

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
            SecretSweepTrigger.Manual, null, CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Succeeded);
        await Assert.That(report.ActiveKeyId).IsNull();
    }

    [Test]
    public async Task Encrypt_WithoutKeys_IsSkipped_AndTouchesNothing()
    {
        _protector.IsConfigured.Returns(false);

        var report = await CreateCoordinator().SweepTenantAsync("t-nokeys", SecretSweepMode.Encrypt,
            SecretSweepTrigger.Manual, null, CancellationToken.None);

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
            SecretSweepTrigger.Manual, null, CancellationToken.None);

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

        await CreateCoordinator().SweepTenantAsync("t-batch", SecretSweepMode.Encrypt, SecretSweepTrigger.Manual, null,
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
            SecretSweepTrigger.Manual, null, CancellationToken.None);

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
            SecretSweepTrigger.Manual, null, CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Skipped);
        await Assert.That(report.Reason).Contains("Win32Exception");
        // Messages of non-engine exceptions (driver, backup tool) may quote connection strings: type name only.
        await Assert.That(report.Reason).DoesNotContain("mongodump not found");
        await _maintenance.DidNotReceiveWithAnyArgs()
            .SweepTenantAsync(default!, default, default(SecretSweepOptions)!, default);
    }

    [Test]
    public async Task Encrypt_BackupDirectoryUnusable_IsSkipped()
    {
        _storage.CreateSecretBackupFilePath("t-baddir").Throws(new UnauthorizedAccessException("denied"));

        var report = await CreateCoordinator().SweepTenantAsync("t-baddir", SecretSweepMode.Encrypt,
            SecretSweepTrigger.Manual, null, CancellationToken.None);

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
            SecretSweepTrigger.Manual, null, CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Succeeded);
        await Assert.That(report.Reason).Contains("not required");
        await Assert.That(report.BackupFileName).IsNull();
        await Assert.That(report.Steps.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Decrypt_IsRefused()
    {
        var report = await CreateCoordinator().SweepTenantAsync("t-decrypt", SecretSweepMode.Decrypt,
            SecretSweepTrigger.Manual, null, CancellationToken.None);

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
            SecretSweepTrigger.Manual, null, CancellationToken.None);

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
            SecretSweepTrigger.Manual, null, CancellationToken.None);

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
            SecretSweepTrigger.Manual, null, CancellationToken.None);

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
            SecretSweepTrigger.Recurring, null, CancellationToken.None);

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
            SecretSweepTrigger.Recurring, null, CancellationToken.None);

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
            SecretSweepTrigger.Recurring, null, CancellationToken.None);

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
            SecretSweepTrigger.Manual, null, CancellationToken.None);

        await Assert.That(report.StrictModeViolation).IsFalse();
        await Assert.That(report.RemainingLegacyValues).IsEqualTo(0);
    }

    [Test]
    public async Task AfterRestore_VerifiesEncryptsVerifies_KeepsUnknownKids_AndListsThemForReEntry()
    {
        // Decisions 2026-10-06, item 2: nothing is cleared after a restore; values of another key ring stay
        // encrypted and become re-entry tasks.
        var rtId = new OctoObjectId("6512a1b2c3d4e5f601020304");
        var unreadable = new SecretSweepUnreadableValue("System.Communication/SftpConfiguration", rtId, "Password", "k7");
        SetupSweep("t-restore", SecretSweepMode.Encrypt, r =>
        {
            AddPlaintext(r, 1);
            r.Totals.Add(SecretValueForm.UnknownKeyId, "k7");
            r.Unreadable.Add(unreadable);
            r.PlaceholdersNormalized = 2;
        });
        SetupVerifySequence("t-restore",
            r =>
            {
                r.Totals.Add(SecretValueForm.UnknownKeyId, "k7");
                r.Unreadable.Add(unreadable);
            },
            r =>
            {
                r.Totals.Add(SecretValueForm.EncV2, "k1");
                r.Totals.Add(SecretValueForm.UnknownKeyId, "k7");
                r.Unreadable.Add(unreadable);
            });

        var report = await CreateCoordinator().RunAfterRestoreAsync("t-restore", null, CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Succeeded);
        await Assert.That(report.Trigger).IsEqualTo(SecretSweepTrigger.Restore);
        await Assert.That(report.Steps.Select(s => s.Mode).ToList()).IsEquivalentTo(new List<SecretSweepMode>
            { SecretSweepMode.Verify, SecretSweepMode.Encrypt, SecretSweepMode.Verify });
        await _maintenance.DidNotReceive().SweepTenantAsync("t-restore", SecretSweepMode.CleanupUnreadable,
            Arg.Any<SecretSweepOptions>(), Arg.Any<CancellationToken>());
        await _maintenance.DidNotReceive().SweepTenantAsync(Arg.Any<string>(), Arg.Any<SecretSweepMode>(),
            Arg.Is<SecretSweepOptions>(o => o.ConfirmCleanupUnreadable), Arg.Any<CancellationToken>());
        // No pre-clear dump any more: nothing destructive runs after a restore.
        await _systemContext.DidNotReceiveWithAnyArgs().BackupTenantAsync(default!, default!);
        await Assert.That(report.BackupFileName).IsNull();

        await Assert.That(report.Unreadable.Count).IsEqualTo(1);
        await Assert.That(report.Unreadable[0].KeyId).IsEqualTo("k7");
        await Assert.That(report.Unreadable[0].RtId).IsEqualTo("6512a1b2c3d4e5f601020304");
        await Assert.That(report.SecretsToReEnter.Count).IsEqualTo(1);
        var secret = report.SecretsToReEnter[0];
        await Assert.That(secret.CkTypeId).IsEqualTo("System.Communication/SftpConfiguration");
        await Assert.That(secret.AttributePath).IsEqualTo("Password");
        await Assert.That(secret.KeyId).IsEqualTo("k7");
        await Assert.That(secret.PreviousForm).IsEqualTo(SecretValueForm.UnknownKeyId);
        await Assert.That(report.Steps.SelectMany(s => s.Cleared)).IsEmpty();
        await Assert.That(report.PlaceholdersNormalized).IsEqualTo(2);
        await _reportStore.Received(1).SaveAsync(report);
    }

    [Test]
    public async Task AfterRestore_RecordsARunWithTheRestoreJobId()
    {
        var runs = new InMemorySecretSweepRunStore();
        SetupSweep("t-restore-run", SecretSweepMode.Encrypt);
        SetupSweep("t-restore-run", SecretSweepMode.Verify);

        await CreateCoordinator(runStore: runs)
            .RunAfterRestoreAsync("t-restore-run", new SecretSweepRunInfo("job-restore"), CancellationToken.None);

        var run = (await runs.GetRunsAsync("t-restore-run")).Single();
        await Assert.That(run.RunId).IsEqualTo("job-restore");
        await Assert.That(run.Trigger).IsEqualTo(SecretSweepTriggerDto.Restore);
        await Assert.That(run.Mode).IsEqualTo(SecretSweepModeDto.Encrypt);
        await Assert.That(run.Outcome).IsEqualTo(SecretSweepOutcomeDto.Succeeded);
        await Assert.That(run.Dump).IsNull();
        await Assert.That(run.TriggeredBy).IsNull();
    }

    [Test]
    public async Task CleanupUnreadable_TakesBackup_ConfirmsToTheEngine_AndListsTheDeletedValues()
    {
        var rtId = new OctoObjectId("6512a1b2c3d4e5f601020304");
        SetupBackupSucceeds();
        SetupSweep("t-cleanup", SecretSweepMode.CleanupUnreadable, r =>
        {
            r.Totals.Add(SecretValueForm.UnknownKeyId, "k7");
            r.Cleared.Add(new SecretSweepClearedValue("Test/Type", rtId, "Password", SecretValueForm.UnknownKeyId, "k7"));
            r.ValuesRewritten = 1;
        });
        SetupSweep("t-cleanup", SecretSweepMode.Verify, r => r.Totals.Add(SecretValueForm.NotSet));

        var report = await CreateCoordinator().SweepTenantAsync("t-cleanup", SecretSweepMode.CleanupUnreadable,
            SecretSweepTrigger.Manual, null, CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Succeeded);
        await Assert.That(report.BackupFileName).IsEqualTo("t-cleanup.presweep.tar.gz");
        await _maintenance.Received(1).SweepTenantAsync("t-cleanup", SecretSweepMode.CleanupUnreadable,
            Arg.Is<SecretSweepOptions>(o => o.ConfirmCleanupUnreadable && !o.ConfirmDecrypt),
            Arg.Any<CancellationToken>());
        // The trailing verify never carries the confirmation.
        await _maintenance.Received(1).SweepTenantAsync("t-cleanup", SecretSweepMode.Verify,
            Arg.Is<SecretSweepOptions>(o => !o.ConfirmCleanupUnreadable), Arg.Any<CancellationToken>());
        await Assert.That(report.Steps[0].Cleared.Count).IsEqualTo(1);
        await Assert.That(report.Unreadable).IsEmpty();
        await Assert.That(report.SecretsToReEnter.Single().RtId).IsEqualTo("6512a1b2c3d4e5f601020304");
    }

    [Test]
    public async Task Encrypt_NeverConfirmsCleanupUnreadable_AndKeepsUnknownKidsAsUnreadable()
    {
        var rtId = new OctoObjectId("6512a1b2c3d4e5f601020304");
        SetupBackupSucceeds();
        SetupSweep("t-enc-unread", SecretSweepMode.Encrypt, r => r.PlaceholdersNormalized = 3);
        SetupSweep("t-enc-unread", SecretSweepMode.Verify, r =>
        {
            r.Totals.Add(SecretValueForm.UnknownKeyId, "k9");
            r.Unreadable.Add(new SecretSweepUnreadableValue("Test/Type", rtId, "ApiKey", "k9"));
        });

        var report = await CreateCoordinator().SweepTenantAsync("t-enc-unread", SecretSweepMode.Encrypt,
            SecretSweepTrigger.Manual, null, CancellationToken.None);

        await _maintenance.Received(1).SweepTenantAsync("t-enc-unread", SecretSweepMode.Encrypt,
            Arg.Is<SecretSweepOptions>(o => !o.ConfirmCleanupUnreadable), Arg.Any<CancellationToken>());
        await Assert.That(report.PlaceholdersNormalized).IsEqualTo(3);
        await Assert.That(report.Unreadable.Single().AttributePath).IsEqualTo("ApiKey");
        await Assert.That(report.SecretsToReEnter.Single().KeyId).IsEqualTo("k9");
    }

    [Test]
    public async Task Run_IsRecordedRunningFirst_ThenWithOutcomeCountsAndDump()
    {
        var runs = new InMemorySecretSweepRunStore();
        var rtId = new OctoObjectId("6512a1b2c3d4e5f601020304");
        _options.BackupRetentionDays = 7;
        SetupBackupSucceeds();
        SetupSweep("t-run", SecretSweepMode.Encrypt, r =>
        {
            // While the sweep runs the history already shows the run as Running with its dump.
            var running = runs.GetRunsAsync("t-run").GetAwaiter().GetResult().Single();
            if (running.Outcome != SecretSweepOutcomeDto.Running || running.Dump == null)
            {
                throw new InvalidOperationException("run not recorded as Running with its dump");
            }

            r.PlaceholdersNormalized = 1;
        });
        SetupSweep("t-run", SecretSweepMode.Verify, r =>
        {
            r.Totals.Add(SecretValueForm.EncV2, "k1");
            r.Totals.Add(SecretValueForm.UnknownKeyId, "k7");
            r.Unreadable.Add(new SecretSweepUnreadableValue("Test/Type", rtId, "Password", "k7"));
        });

        await CreateCoordinator(runStore: runs).SweepTenantAsync("t-run", SecretSweepMode.Encrypt,
            SecretSweepTrigger.Manual, new SecretSweepRunInfo("job-42", "alice"), CancellationToken.None);

        var run = (await runs.GetRunsAsync("t-run")).Single();
        await Assert.That(run.RunId).IsEqualTo("job-42");
        await Assert.That(run.TriggeredBy).IsEqualTo("alice");
        await Assert.That(run.Mode).IsEqualTo(SecretSweepModeDto.Encrypt);
        await Assert.That(run.Trigger).IsEqualTo(SecretSweepTriggerDto.Manual);
        await Assert.That(run.Outcome).IsEqualTo(SecretSweepOutcomeDto.Succeeded);
        await Assert.That(run.StartedAt).IsEqualTo(_time.GetUtcNow().UtcDateTime);
        await Assert.That(run.CompletedAt).IsEqualTo(_time.GetUtcNow().UtcDateTime);
        await Assert.That(run.Totals.EncV2).IsEqualTo(1);
        await Assert.That(run.Totals.UnknownKeyId).IsEqualTo(1);
        await Assert.That(run.Totals.Total).IsEqualTo(2);
        await Assert.That(run.PlaceholdersNormalized).IsEqualTo(1);
        await Assert.That(run.UnreadableCount).IsEqualTo(1);
        await Assert.That(run.Dump).IsNotNull();
        await Assert.That(run.Dump!.FileName).IsEqualTo("t-run.presweep.tar.gz");
        await Assert.That(run.Dump.Exists).IsTrue();
        await Assert.That(run.Dump.SizeBytes).IsEqualTo(4);
        await Assert.That(run.Dump.CreatedAt).IsEqualTo(_time.GetUtcNow().UtcDateTime);
        await Assert.That(run.Dump.ExpiresAt).IsEqualTo(_time.GetUtcNow().UtcDateTime.AddDays(7));
        await Assert.That(run.Dump.DeletedAt).IsNull();
    }

    [Test]
    public async Task Run_Verify_HasNoDump_AndAGeneratedIdOutsideAJob()
    {
        var runs = new InMemorySecretSweepRunStore();
        SetupSweep("t-run-verify", SecretSweepMode.Verify);

        await CreateCoordinator(runStore: runs).SweepTenantAsync("t-run-verify", SecretSweepMode.Verify,
            SecretSweepTrigger.Recurring, null, CancellationToken.None);

        var run = (await runs.GetRunsAsync("t-run-verify")).Single();
        await Assert.That(run.RunId).IsNotEmpty();
        await Assert.That(run.Dump).IsNull();
        await Assert.That(run.Trigger).IsEqualTo(SecretSweepTriggerDto.Recurring);
    }

    [Test]
    public async Task Run_SkippedSweep_IsRecordedAsSkipped()
    {
        var runs = new InMemorySecretSweepRunStore();
        _protector.IsConfigured.Returns(false);

        await CreateCoordinator(runStore: runs).SweepTenantAsync("t-run-skip", SecretSweepMode.Encrypt,
            SecretSweepTrigger.Manual, new SecretSweepRunInfo("job-1", "bob"), CancellationToken.None);

        await Assert.That((await runs.GetRunsAsync("t-run-skip")).Single().Outcome)
            .IsEqualTo(SecretSweepOutcomeDto.Skipped);
    }

    [Test]
    public async Task Run_DumpDeletedWhileRunning_StaysDeleted()
    {
        var runs = new InMemorySecretSweepRunStore();
        var deletedAt = new DateTime(2026, 10, 6, 12, 30, 0, DateTimeKind.Utc);
        SetupBackupSucceeds();
        SetupSweep("t-run-del", SecretSweepMode.Encrypt, _ =>
            runs.MarkDumpDeletedAsync("t-run-del", "t-run-del.presweep.tar.gz", deletedAt, "carol")
                .GetAwaiter().GetResult());
        SetupSweep("t-run-del", SecretSweepMode.Verify);

        await CreateCoordinator(runStore: runs).SweepTenantAsync("t-run-del", SecretSweepMode.Encrypt,
            SecretSweepTrigger.Manual, new SecretSweepRunInfo("job-7"), CancellationToken.None);

        var dump = (await runs.GetRunsAsync("t-run-del")).Single().Dump!;
        await Assert.That(dump.DeletedAt).IsEqualTo(deletedAt);
        await Assert.That(dump.DeletedBy).IsEqualTo("carol");
        await Assert.That(dump.Exists).IsFalse();
    }

    [Test]
    public async Task RunStoreFailure_DoesNotFailTheSweep()
    {
        var runs = Substitute.For<ISecretSweepRunStore>();
        runs.UpsertAsync(Arg.Any<string>(), Arg.Any<SecretSweepRunDto>())
            .ThrowsAsync(new InvalidOperationException("storage down"));
        SetupSweep("t-run-down", SecretSweepMode.Verify);

        var report = await CreateCoordinator(runStore: runs).SweepTenantAsync("t-run-down", SecretSweepMode.Verify,
            SecretSweepTrigger.Manual, null, CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Succeeded);
    }

    [Test]
    public async Task Sweep_TenantBusy_IsSkipped_AndTouchesNothing()
    {
        var tenantLock = Substitute.For<ISecretSweepTenantLock>();
        tenantLock.TryAcquire("t-busy", Arg.Any<TimeSpan>()).Returns((IDisposable?)null);

        var report = await CreateCoordinator(tenantLock)
            .SweepTenantAsync("t-busy", SecretSweepMode.Encrypt, SecretSweepTrigger.Manual, null, CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Skipped);
        await Assert.That(report.Reason).Contains("Another secret sweep");
        await _maintenance.DidNotReceiveWithAnyArgs()
            .SweepTenantAsync(default!, default, default(SecretSweepOptions)!, default);
        await _systemContext.DidNotReceiveWithAnyArgs().BackupTenantAsync(default!, default!);
    }

    [Test]
    public async Task Sweep_HoldsTheTenantLockDuringTheSweep_AndReleasesIt()
    {
        var handle = Substitute.For<IDisposable>();
        var tenantLock = Substitute.For<ISecretSweepTenantLock>();
        tenantLock.TryAcquire("t-lock", Arg.Any<TimeSpan>()).Returns(handle);
        _maintenance.SweepTenantAsync("t-lock", SecretSweepMode.Verify, Arg.Any<SecretSweepOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                handle.DidNotReceive().Dispose();
                return Task.FromResult(new SecretSweepResult("t-lock", SecretSweepMode.Verify));
            });

        await CreateCoordinator(tenantLock)
            .SweepTenantAsync("t-lock", SecretSweepMode.Verify, SecretSweepTrigger.Manual, null, CancellationToken.None);

        handle.Received(1).Dispose();
        tenantLock.Received(1).TryAcquire("t-lock", SecretSweepCoordinator.SweepLockTimeout);
    }

    [Test]
    public async Task AfterRestore_WaitsLongerForTheTenantLock_AndIsSkippedWhenItStaysBusy()
    {
        var tenantLock = Substitute.For<ISecretSweepTenantLock>();
        tenantLock.TryAcquire("t-restore-busy", Arg.Any<TimeSpan>()).Returns((IDisposable?)null);

        var report = await CreateCoordinator(tenantLock)
            .RunAfterRestoreAsync("t-restore-busy", null, CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Skipped);
        tenantLock.Received(1).TryAcquire("t-restore-busy", SecretSweepCoordinator.RestoreLockTimeout);
        await _maintenance.DidNotReceiveWithAnyArgs()
            .SweepTenantAsync(default!, default, default(SecretSweepOptions)!, default);
    }

    [Test]
    public async Task AfterRestore_WithoutKeys_IsSkipped()
    {
        _protector.IsConfigured.Returns(false);

        var report = await CreateCoordinator().RunAfterRestoreAsync("t-restore-nokeys", null, CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Skipped);
        await _maintenance.DidNotReceiveWithAnyArgs()
            .SweepTenantAsync(default!, default, default(SecretSweepOptions)!, default);
    }

    [Test]
    public async Task AfterRestore_DisabledByConfiguration_IsSkipped()
    {
        _options.RunAfterRestore = false;

        var report = await CreateCoordinator().RunAfterRestoreAsync("t-restore-off", null, CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Skipped);
        await Assert.That(report.Reason).Contains("RunAfterRestore");
    }

    [Test]
    public async Task AfterRestore_SweepThrows_IsReportedNotThrown()
    {
        _maintenance.SweepTenantAsync("t-restore-err", SecretSweepMode.Verify,
                Arg.Any<SecretSweepOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var report = await CreateCoordinator().RunAfterRestoreAsync("t-restore-err", null, CancellationToken.None);

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
