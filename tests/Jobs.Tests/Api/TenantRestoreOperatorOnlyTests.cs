using System.Net;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure.Configuration;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Api;

/// <summary>
///     The restore of an uploaded archive writes the archive's documents into the tenant database as they are, so an
///     edited archive can move protected values between attributes. Only a system operator (a user of the system
///     tenant with <c>TenantManagement</c>) may start it; a tenant identity may not, also not with
///     <c>TenantManagement</c> of its own or of a parent tenant. Dumps stay with the tenant administrators.
/// </summary>
internal class TenantRestoreOperatorOnlyTests
{
    private const string Parent = JobsApiTestHost.Parent;
    private const string Child = JobsApiTestHost.Child;
    private const string SystemTenant = JobsApiTestHost.SystemTenant;
    private const string RestorePath = $"/{Child}/v1/jobs/restore-from-upload?tusFileId=upload-1&databaseName=octo-child";

    [Test]
    public async Task OwnTenantAdministrator_IsRefused_AndNothingIsEnqueued()
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.PostAsync(RestorePath,
            JobsApiTestHost.UserTokenWithRoles(Child, CommonConstants.TenantManagementRole));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(0);
    }

    [Test]
    public async Task ParentTenantAdministrator_IsRefused_AndNothingIsEnqueued()
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.PostAsync(RestorePath,
            JobsApiTestHost.UserTokenWithRoles(Parent, CommonConstants.TenantManagementRole));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(0);
    }

    [Test]
    public async Task OwnTenantServiceToken_IsRefused_AndNothingIsEnqueued()
    {
        using var host = await JobsApiTestHost.StartAsync(
            options => options.ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Enforce);

        var response = await host.PostAsync(RestorePath, JobsApiTestHost.ServiceToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(0);
    }

    [Test]
    public async Task SystemTenantUserWithoutTenantManagement_IsRefused()
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.PostAsync(RestorePath,
            JobsApiTestHost.UserTokenWithRoles(SystemTenant, CommonConstants.AdminPanelManagementRole));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(0);
    }

    [Test]
    public async Task SystemOperator_RestoresIntoTheChildTenant()
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.PostAsync(RestorePath, JobsApiTestHost.SystemOperatorToken());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(1);
        await Assert.That(JobTenantBinding.TryResolveTenantId(host.LastEnqueuedJob())).IsEqualTo(Child);
    }

    [Test]
    public async Task OwnTenantAdministrator_StillDumpsTheTenant()
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.PostAsync($"/{Child}/v1/jobs/dump-repository",
            JobsApiTestHost.UserTokenWithRoles(Child, CommonConstants.TenantManagementRole));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(1);
    }
}
