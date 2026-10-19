using System.Net;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Backend.Jobs.Tests.Api;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Communication.Contracts;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs;

/// <summary>
///     AB#6734 — pins the decision (Gerald, 2026-10-16) that a tenant dump is a whole-tenant backup by design: it is
///     a physical mongodump of the tenant database and therefore bypasses the row-level data permissions of the
///     requesting user. The protection is the gate, not a row filter: dump, restore, archive export and import
///     need the tenant role <c>TenantManagement</c> (AB#4497) on top of the tenant gate. The runtime-data exports
///     (<see cref="ExportModelJobCallerTests" />) are the opposite: they run with the caller's permissions.
/// </summary>
public class DumpWholeTenantByDesignTests
{
    [Test]
    public async Task DumpJob_IsAPhysicalBackupOfTheTenant_WithoutAnyCallerContextOrSession()
    {
        var systemContext = Substitute.For<ISystemContext>();
        var backupFileStorage = Substitute.For<IBackupFileStorageService>();
        var tenantContext = Substitute.For<ITenantContext>();
        var expectedPath = Path.Combine(Path.GetTempPath(), "tenant-1-dump.tar.gz");
        systemContext.IsSystemTenantExistingAsync().Returns(true);
        systemContext.FindTenantContextAsync("tenant-1").Returns(tenantContext);
        backupFileStorage.GenerateDumpFileName("tenant-1").Returns("tenant-1-dump.tar.gz");
        backupFileStorage.GetDumpFilePath("tenant-1", "tenant-1-dump.tar.gz").Returns(expectedPath);
        systemContext.BackupTenantAsync("tenant-1", expectedPath, timeout: TimeSpan.FromHours(1))
            .Returns(new CommandResult { Success = true });
        var job = new DumpRepositoryJob(Substitute.For<ILogger<DumpRepositoryJob>>(), systemContext, backupFileStorage);

        var result = await job.Run("tenant-1", false, null);

        await Assert.That(result).IsEqualTo(expectedPath);
        // The whole tenant database is dumped; no repository session (and therefore no data permission) is involved.
        await systemContext.Received(1).BackupTenantAsync("tenant-1", expectedPath, timeout: TimeSpan.FromHours(1));
        await systemContext.DidNotReceive().FindTenantRepositoryAsync(Arg.Any<string>());
    }

    [Test]
    public async Task DumpJobContract_CarriesNoCallerContext()
    {
        var run = typeof(IDumpRepositoryJob).GetMethod(nameof(IDumpRepositoryJob.Run))!;

        await Assert.That(run.GetParameters().Select(p => p.ParameterType))
            .DoesNotContain(typeof(RtSecurityContext));
        await Assert.That(run.GetParameters().Select(p => p.Name!))
            .IsEquivalentTo(new[] { "tenantId", "includeArchiveData", "cancellationToken" });
    }

    [Test]
    public async Task Api_TenantManagementCallerGetsTheDump_CallerWithoutTheRoleGets403AndNoJob()
    {
        using var host = await JobsApiTestHost.StartAsync();

        var withRole = await host.PostAsync($"/{JobsApiTestHost.Child}/v1/jobs/dump-repository",
            JobsApiTestHost.UserTokenWithRoles(JobsApiTestHost.Child, CommonConstants.TenantManagementRole));
        await Assert.That(withRole.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var args = host.LastEnqueuedJob().Args;
        await Assert.That(args.Count).IsEqualTo(3); // tenantId, includeArchiveData, cancellation token: no caller
        host.ResetJobClient();

        var withoutRole = await host.PostAsync($"/{JobsApiTestHost.Child}/v1/jobs/dump-repository",
            JobsApiTestHost.UserTokenWithRoles(JobsApiTestHost.Child, CommonConstants.AdminPanelManagementRole));
        await Assert.That(withoutRole.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(0);
    }
}
