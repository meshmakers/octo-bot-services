using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Meshmakers.Octo.Backend.BotServices.Methods;
using Meshmakers.Octo.Backend.BotServices.Services.DumpRegistry;
using Meshmakers.Octo.Backend.Jobs.DumpRegistry;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Methods;

public class RestoreArchiveCheckMethodTests
{
    private const string Tenant = "tenant-1";
    private const string SystemTenant = "octosystem";
    private const string JobId = "job-9";
    private const string UploadId = "upload-1";

    private readonly IBackgroundJobClient _jobs = Substitute.For<IBackgroundJobClient>();
    private readonly GenericFakeJobStorage _storage = new();
    private readonly RecordingOperationProgress _progress = new();
    private readonly ISharedUploadStore _uploads = Substitute.For<ISharedUploadStore>();
    private readonly IBackupFileStorageService _backupFiles = Substitute.For<IBackupFileStorageService>();
    private Job? _enqueued;

    public RestoreArchiveCheckMethodTests()
    {
        _jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns(call =>
        {
            _enqueued = call.ArgAt<Job>(0);
            return JobId;
        });
        _uploads.GetAsync(Tenant, UploadId, Arg.Any<CancellationToken>())
            .Returns(new SharedUpload(UploadId, 1024, ""));
        _uploads.IsCompleteAsync(Tenant, UploadId, Arg.Any<CancellationToken>()).Returns(true);
        _backupFiles.GetTusUploadFilePath(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Path.Combine(Path.GetTempPath(), $"no-such-upload-{Guid.NewGuid():N}"));
    }

    public static IEnumerable<Func<(string? TokenTenant, string[] Roles, ArchiveRegistrationCheck Expected)>> Callers()
    {
        yield return () => (Tenant, ["TenantManagement"], ArchiveRegistrationCheck.Required);
        yield return () => ("parent-tenant", ["TenantManagement"], ArchiveRegistrationCheck.Required);
        yield return () => (SystemTenant, ["AdminPanelManagement"], ArchiveRegistrationCheck.Required);
        yield return () => (null, ["TenantManagement"], ArchiveRegistrationCheck.Required);
        yield return () => ("OctoSystem", ["tenantmanagement"], ArchiveRegistrationCheck.NotRequired);
    }

    [Test]
    [MethodDataSource(nameof(Callers))]
    public async Task TheMethodCallerDecidesTheArchiveCheck(string? tokenTenant, string[] roles,
        ArchiveRegistrationCheck expected)
    {
        var archiveCheck = Policy().For(Caller(tokenTenant, roles));

        await Assert.That(archiveCheck).IsEqualTo(expected);
    }

    [Test]
    [Arguments(ArchiveRegistrationCheck.Required)]
    [Arguments(ArchiveRegistrationCheck.NotRequired)]
    public async Task TheWork_EnqueuesTheJobWithTheArchiveCheck(ArchiveRegistrationCheck archiveCheck)
    {
        _storage.States.Enqueue("Succeeded");

        await Handler().RunAsync(Tenant, "db1", UploadId, null, false, archiveCheck, "user-1", null, _progress,
            CancellationToken.None);

        await Assert.That(_enqueued!.Method.Name).IsEqualTo(nameof(IRestoreRepositoryJob.RunForOperation));
        await Assert.That(_enqueued.Args[6]).IsEqualTo(archiveCheck);
    }

    [Test]
    public async Task WithoutACallerPrincipal_TheArchiveMustBeRegistered()
    {
        var policy = Policy();

        await Assert.That(policy.For((CkMethodPrincipal?)null)).IsEqualTo(ArchiveRegistrationCheck.Required);
    }

    [Test]
    public async Task ARefusedArchive_IsRestoreFailedWithReasonArchiveNotRegistered()
    {
        _storage.FailedExceptionType = typeof(ArchiveNotRegisteredException).FullName;
        _storage.States.Enqueue("Deleted");

        var outcome = await Handler().RunAsync(Tenant, "db1", UploadId, null, false,
            ArchiveRegistrationCheck.Required, "user-1", null, _progress, CancellationToken.None);

        await Assert.That(outcome.Failure!.Code).IsEqualTo(TenantRepositoryRestoreHandler.RestoreFailed);
        await Assert.That(outcome.Failure.Parameters["reason"]).IsEqualTo("archive-not-registered");
        await Assert.That(outcome.Failure.Parameters["jobId"]).IsEqualTo(JobId);
    }

    [Test]
    public async Task AnotherFailure_HasNoArchiveReason()
    {
        _storage.FailedExceptionType = typeof(JobFailedException).FullName;
        _storage.States.Enqueue("Deleted");

        var outcome = await Handler().RunAsync(Tenant, "db1", UploadId, null, false,
            ArchiveRegistrationCheck.Required, "user-1", null, _progress, CancellationToken.None);

        await Assert.That(outcome.Failure!.Code).IsEqualTo(TenantRepositoryRestoreHandler.RestoreFailed);
        await Assert.That(outcome.Failure.Parameters.ContainsKey("reason")).IsFalse();
    }

    private static CkMethodPrincipal Caller(string? tokenTenant, string[] roles) =>
        new("user-1", roles, ["octo_api"]) { TokenTenantId = tokenTenant };

    private static RestoreArchiveCheckPolicy Policy() =>
        new(Options.Create(new OctoSystemConfiguration { SystemTenantId = "OctoSystem" }));

    private TenantRepositoryRestoreHandler Handler() =>
        new(_jobs, _storage, _uploads, _backupFiles,
            Options.Create(new BotJobMethodOptions { PollInterval = TimeSpan.FromMilliseconds(1) }),
            TimeProvider.System, NullLogger<TenantRepositoryRestoreHandler>.Instance, Policy());
}
