using System.Net;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.Jobs.DumpRegistry;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure.Configuration;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Api;

internal class TenantRestoreArchiveCheckTests
{
    private const string Parent = JobsApiTestHost.Parent;
    private const string Child = JobsApiTestHost.Child;
    private const string SystemTenant = JobsApiTestHost.SystemTenant;
    private const string RestorePath = $"/{Child}/v1/jobs/restore-from-upload?tusFileId=upload-1&databaseName=octo-child";

    [Test]
    public async Task OwnTenantAdministrator_EnqueuesARestoreThatRequiresARegisteredDump()
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.PostAsync(RestorePath,
            JobsApiTestHost.UserTokenWithRoles(Child, CommonConstants.TenantManagementRole));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(EnqueuedArchiveCheck(host)).IsEqualTo(ArchiveRegistrationCheck.Required);
    }

    [Test]
    public async Task ParentTenantAdministrator_EnqueuesARestoreThatRequiresARegisteredDump()
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.PostAsync(RestorePath,
            JobsApiTestHost.UserTokenWithRoles(Parent, CommonConstants.TenantManagementRole));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(EnqueuedArchiveCheck(host)).IsEqualTo(ArchiveRegistrationCheck.Required);
        await Assert.That(JobTenantBinding.TryResolveTenantId(host.LastEnqueuedJob())).IsEqualTo(Child);
    }

    [Test]
    public async Task OwnTenantServiceToken_EnqueuesARestoreThatRequiresARegisteredDump()
    {
        using var host = await JobsApiTestHost.StartAsync(
            options => options.ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Enforce);

        var response = await host.PostAsync(RestorePath, JobsApiTestHost.ServiceToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(EnqueuedArchiveCheck(host)).IsEqualTo(ArchiveRegistrationCheck.Required);
    }

    [Test]
    public async Task SystemOperator_EnqueuesARestoreOfAnyArchive()
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.PostAsync(RestorePath,
            JobsApiTestHost.UserTokenWithRoles(SystemTenant, CommonConstants.TenantManagementRole));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(EnqueuedArchiveCheck(host)).IsEqualTo(ArchiveRegistrationCheck.NotRequired);
        await Assert.That(JobTenantBinding.TryResolveTenantId(host.LastEnqueuedJob())).IsEqualTo(Child);
    }

    private static ArchiveRegistrationCheck EnqueuedArchiveCheck(JobsApiTestHost host)
    {
        var job = host.LastEnqueuedJob();
        var archiveCheckIndex = Array.FindIndex(job.Method.GetParameters(), p => p.Name == "archiveCheck");
        return (ArchiveRegistrationCheck)job.Args[archiveCheckIndex]!;
    }
}
