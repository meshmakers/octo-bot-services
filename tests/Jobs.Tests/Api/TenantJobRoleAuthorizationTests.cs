using System.Net;
using Meshmakers.Octo.Communication.Contracts;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Api;

/// <summary>
///     AB#4497 / AB#6848 — dump, restore, archive export, archive import, the fixup script run and the download of their artifacts need the
///     tenant role <c>TenantManagement</c> on top of the <c>octo_api</c> scope. A token with the scope but
///     without the role answers <c>403</c> and no job is enqueued. For a parent-tenant caller the role is the one
///     in the caller's own (parent) tenant.
/// </summary>
internal class TenantJobRoleAuthorizationTests
{
    private const string Parent = JobsApiTestHost.Parent;
    private const string Child = JobsApiTestHost.Child;

    private const string ExportQuery = "?archiveRtId=6512a1b2c3d4e5f601020304";
    private const string RestoreQuery = "?tusFileId=upload-1&databaseName=octo-child";
    private const string ImportQuery = "?tusFileId=upload-1&archiveRtId=6512a1b2c3d4e5f601020304";

    /// <summary>Scope octo_api, but none of the roles.</summary>
    [Test]
    [Arguments("run-fixup-scripts", "")]
    [Arguments("dump-repository", "")]
    [Arguments("export-archive-data", ExportQuery)]
    [Arguments("restore-from-upload", RestoreQuery)]
    [Arguments("import-archive-data-from-upload", ImportQuery)]
    public async Task OwnTenantUserWithoutAnyRole_IsForbidden_AndNothingIsEnqueued(string route, string query)
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.PostAsync($"/{Child}/v1/jobs/{route}{query}",
            JobsApiTestHost.UserTokenWithRoles(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(0);
    }

    /// <summary>Other administrator roles do not substitute TenantManagement.</summary>
    [Test]
    [Arguments("run-fixup-scripts", "")]
    [Arguments("dump-repository", "")]
    [Arguments("export-archive-data", ExportQuery)]
    [Arguments("restore-from-upload", RestoreQuery)]
    [Arguments("import-archive-data-from-upload", ImportQuery)]
    public async Task OtherRoles_AreNotEnough(string route, string query)
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.PostAsync($"/{Child}/v1/jobs/{route}{query}",
            JobsApiTestHost.UserTokenWithRoles(Child, CommonConstants.AdminPanelManagementRole,
                CommonConstants.UserManagementRole, CommonConstants.SecretManagementRole));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(0);
    }

    [Test]
    [Arguments("run-fixup-scripts", "")]
    [Arguments("dump-repository", "")]
    [Arguments("export-archive-data", ExportQuery)]
    [Arguments("restore-from-upload", RestoreQuery)]
    [Arguments("import-archive-data-from-upload", ImportQuery)]
    public async Task OwnTenantUserWithTenantManagement_IsAllowed_AndEnqueues(string route, string query)
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.PostAsync($"/{Child}/v1/jobs/{route}{query}",
            JobsApiTestHost.UserTokenWithRoles(Child, CommonConstants.TenantManagementRole));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(1);
    }

    /// <summary>A parent-tenant user without the role in the parent tenant gets 403 on the child route.</summary>
    [Test]
    [Arguments("run-fixup-scripts", "")]
    [Arguments("dump-repository", "")]
    [Arguments("export-archive-data", ExportQuery)]
    [Arguments("restore-from-upload", RestoreQuery)]
    [Arguments("import-archive-data-from-upload", ImportQuery)]
    public async Task ParentUserWithoutRole_IsForbiddenOnChildRoute(string route, string query)
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.PostAsync($"/{Child}/v1/jobs/{route}{query}",
            JobsApiTestHost.UserTokenWithRoles(Parent));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(0);
    }

    /// <summary>A parent-tenant user with TenantManagement (in the parent tenant) may administer the child.</summary>
    [Test]
    [Arguments("run-fixup-scripts", "")]
    [Arguments("dump-repository", "")]
    [Arguments("export-archive-data", ExportQuery)]
    [Arguments("restore-from-upload", RestoreQuery)]
    [Arguments("import-archive-data-from-upload", ImportQuery)]
    public async Task ParentUserWithTenantManagement_IsAllowedOnChildRoute(string route, string query)
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.PostAsync($"/{Child}/v1/jobs/{route}{query}",
            JobsApiTestHost.UserTokenWithRoles(Parent, CommonConstants.TenantManagementRole));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(1);
    }

    /// <summary>
    ///     The artifact download (dump / export file) is part of the operation: no role, no file — on the tenant
    ///     route and on the deprecated system route alike.
    /// </summary>
    [Test]
    [Arguments("/childtenant/v1/jobs/download?id=job-1")]
    [Arguments("/system/v1/jobs/download?id=job-1")]
    public async Task DownloadWithoutRole_IsForbidden(string path)
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.GetAsync(path, JobsApiTestHost.UserTokenWithRoles(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }
}
