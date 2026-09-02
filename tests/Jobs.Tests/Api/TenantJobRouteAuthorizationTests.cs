using System.Net;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using TenantJobsController = Meshmakers.Octo.Backend.BotServices.TenantApi.v1.Controllers.JobsController;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Api;

/// <summary>
///     AB#5060 — the tenant-routed job operations (<c>{tenantId}/v1/jobs/...</c>) and the transport
///     tenant gate that now reaches them.
/// </summary>
/// <remarks>
///     <para>
///         These run through a real request pipeline (see <see cref="JobsApiTestHost" />) hosting the
///         two real <c>JobsController</c>s behind the real <c>UseOctoTenantAuthorization()</c>
///         middleware. Calling a controller method directly would prove nothing here: the whole point
///         of moving the tenant from <c>?tenantId=</c> into the route is that a piece of
///         <b>middleware</b> reads the route value, so the gate can only be observed from outside the
///         endpoint.
///     </para>
///     <para>
///         The scenarios per route are the contract of AB#5060: own tenant allowed (the equality case,
///         unchanged), a <b>parent user</b> token allowed on a child route (the new case, opened by
///         <c>[AllowParentTenantAdministration]</c>), an unrelated tenant refused, a <b>service</b>
///         token never admitted by the ancestor rule, and the same effect as the System-API variant it
///         replaces. What happens to the <i>artifact</i> those jobs produce is AB#5070 and lives in
///         <see cref="JobArtifactTenantBindingTests" />.
///     </para>
/// </remarks>
internal class TenantJobRouteAuthorizationTests
{
    private const string Parent = JobsApiTestHost.Parent;
    private const string Child = JobsApiTestHost.Child;
    private const string Unrelated = JobsApiTestHost.Unrelated;

    // The five tenant-addressed operations of this service, with the query arguments each needs
    // beyond the tenant. Both controllers serve exactly this set.
    private const string ExportQuery = "?archiveRtId=6512a1b2c3d4e5f601020304";
    private const string RestoreQuery = "?tusFileId=upload-1&databaseName=octo-child";
    private const string ImportQuery = "?tusFileId=upload-1&archiveRtId=6512a1b2c3d4e5f601020304";

    /// <summary>The equality case: a user token of the addressed tenant. Unchanged by AB#5060.</summary>
    [Test]
    [Arguments("run-fixup-scripts", "")]
    [Arguments("dump-repository", "")]
    [Arguments("export-archive-data", ExportQuery)]
    [Arguments("restore-from-upload", RestoreQuery)]
    [Arguments("import-archive-data-from-upload", ImportQuery)]
    public async Task TenantRoute_OwnTenantUserToken_IsAllowed(string route, string query)
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.PostAsync($"/{Child}/v1/jobs/{route}{query}", JobsApiTestHost.UserToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    ///     🔴 The new case. A user token issued for the <b>parent</b> tenant reaches the child's
    ///     administration route, because the endpoint carries
    ///     <c>[AllowParentTenantAdministration]</c>. That is administration, not access to the child's
    ///     data: no data route of this service is marked.
    /// </summary>
    [Test]
    [Arguments("run-fixup-scripts", "")]
    [Arguments("dump-repository", "")]
    [Arguments("export-archive-data", ExportQuery)]
    [Arguments("restore-from-upload", RestoreQuery)]
    [Arguments("import-archive-data-from-upload", ImportQuery)]
    public async Task TenantRoute_ParentUserToken_IsAllowedOnChildRoute(string route, string query)
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.PostAsync($"/{Child}/v1/jobs/{route}{query}", JobsApiTestHost.UserToken(Parent));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    ///     A user token of a tenant that is neither the addressed one nor its parent is refused. The
    ///     marker widens the gate by exactly one relation, never into a blanket relaxation.
    /// </summary>
    [Test]
    [Arguments("run-fixup-scripts", "")]
    [Arguments("dump-repository", "")]
    [Arguments("export-archive-data", ExportQuery)]
    [Arguments("restore-from-upload", RestoreQuery)]
    [Arguments("import-archive-data-from-upload", ImportQuery)]
    public async Task TenantRoute_UnrelatedUserToken_IsForbidden(string route, string query)
    {
        using var host = await JobsApiTestHost.StartAsync();

        var response = await host.PostAsync($"/{Child}/v1/jobs/{route}{query}", JobsApiTestHost.UserToken(Unrelated));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    /// <summary>
    ///     🔴 A <b>service</b> token of the parent is refused on the child's route even though the
    ///     endpoint is marked: the ancestor rule is user-token only, because a client-credentials
    ///     token's <c>tenant_id</c> proves nothing (mirrored clients share the parent's secret). Run
    ///     with <c>ServiceTokenEnforcement = Enforce</c>, because the platform default
    ///     (<c>LogOnly</c>) changes no outcome and would hide the refusal behind the AB#5032 staging.
    /// </summary>
    [Test]
    [Arguments("run-fixup-scripts", "")]
    [Arguments("dump-repository", "")]
    [Arguments("export-archive-data", ExportQuery)]
    [Arguments("restore-from-upload", RestoreQuery)]
    [Arguments("import-archive-data-from-upload", ImportQuery)]
    public async Task TenantRoute_ParentServiceToken_IsNotAllowedByTheAncestorRule(string route, string query)
    {
        using var host = await JobsApiTestHost.StartAsync(
            o => o.ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Enforce);

        var response = await host.PostAsync($"/{Child}/v1/jobs/{route}{query}", JobsApiTestHost.ServiceToken(Parent));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    /// <summary>
    ///     A service token issued for the addressed tenant itself keeps working — the rule above
    ///     removes the ancestor shortcut, not the exact match every deployed worker relies on.
    /// </summary>
    [Test]
    [Arguments("run-fixup-scripts", "")]
    [Arguments("dump-repository", "")]
    [Arguments("export-archive-data", ExportQuery)]
    [Arguments("restore-from-upload", RestoreQuery)]
    [Arguments("import-archive-data-from-upload", ImportQuery)]
    public async Task TenantRoute_OwnServiceToken_IsAllowed(string route, string query)
    {
        using var host = await JobsApiTestHost.StartAsync(
            o => o.ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Enforce);

        var response = await host.PostAsync($"/{Child}/v1/jobs/{route}{query}", JobsApiTestHost.ServiceToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    ///     Same operation, same effect: the tenant route enqueues the identical Hangfire job — same
    ///     job type, same method, same arguments — as the deprecated System-API route it replaces.
    ///     That is what makes the System variant safe to keep as a fallback until stage 3 removes it,
    ///     and it is checked rather than argued because the two surfaces are two controllers.
    /// </summary>
    [Test]
    [Arguments("run-fixup-scripts", "")]
    [Arguments("dump-repository", "")]
    [Arguments("export-archive-data", ExportQuery)]
    [Arguments("restore-from-upload", RestoreQuery)]
    [Arguments("import-archive-data-from-upload", ImportQuery)]
    public async Task TenantRoute_EnqueuesTheSameJobAsTheSystemRoute(string route, string query)
    {
        using var host = await JobsApiTestHost.StartAsync();

        var tenantResponse = await host.PostAsync($"/{Child}/v1/jobs/{route}{query}",
            JobsApiTestHost.UserToken(Child));
        await Assert.That(tenantResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var viaTenantRoute = host.LastEnqueuedJob();

        host.ResetJobClient();

        var separator = query.Length == 0 ? "?" : "&";
        var systemResponse = await host.PostAsync($"/system/v1/jobs/{route}{query}{separator}tenantId={Child}",
            JobsApiTestHost.UserToken(Child));
        await Assert.That(systemResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var viaSystemRoute = host.LastEnqueuedJob();

        await Assert.That(viaTenantRoute.Type).IsEqualTo(viaSystemRoute.Type);
        await Assert.That(viaTenantRoute.Method.Name).IsEqualTo(viaSystemRoute.Method.Name);
        var sameArguments = viaTenantRoute.Args.Select(a => a?.ToString())
            .SequenceEqual(viaSystemRoute.Args.Select(a => a?.ToString()));
        await Assert.That(sameArguments).IsTrue();
    }

    /// <summary>
    ///     The System-API variants of the five <i>enqueueing</i> operations keep working untouched —
    ///     including for a caller whose token was issued for a different tenant, because the gate reads
    ///     the route value and that route has none. That is precisely the hole the tenant routes close;
    ///     pinning it here records that removing the System variants (stage 3 of AB#5060) is the fix,
    ///     not a regression.
    /// </summary>
    /// <remarks>
    ///     🔴 Enqueueing only. The System <i>artifact</i> routes are no longer ungated: AB#5070 gave
    ///     them the check the middleware cannot perform there, because an open artifact endpoint could
    ///     not wait for stage 3. See <see cref="JobArtifactTenantBindingTests" />.
    /// </remarks>
    [Test]
    [Arguments("run-fixup-scripts", "")]
    [Arguments("dump-repository", "")]
    [Arguments("export-archive-data", ExportQuery)]
    [Arguments("restore-from-upload", RestoreQuery)]
    [Arguments("import-archive-data-from-upload", ImportQuery)]
    public async Task SystemRoute_StaysFunctionalAndUngated(string route, string query)
    {
        using var host = await JobsApiTestHost.StartAsync();

        var separator = query.Length == 0 ? "?" : "&";
        var response = await host.PostAsync($"/system/v1/jobs/{route}{query}{separator}tenantId={Child}",
            JobsApiTestHost.UserToken(Unrelated));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    ///     🔴 The marker is what opens the parent path, so where it sits is part of the contract: on
    ///     the tenant job routes and on nothing else in this service. In particular it must never
    ///     reach a route that returns tenant content other than the artifact of an administration
    ///     operation the same caller was allowed to start (AB#5070).
    /// </summary>
    [Test]
    public async Task OnlyTheTenantJobRoutesCarryTheParentAdministrationMarker()
    {
        var marked = typeof(TenantJobsController).Assembly.GetTypes()
            .Where(IsMarked)
            .ToArray();

        await Assert.That(marked.Length).IsEqualTo(1);
        await Assert.That(marked[0]).IsEqualTo(typeof(TenantJobsController));

        static bool IsMarked(Type type)
        {
            return type.GetCustomAttributes(typeof(IAllowParentTenantAdministration), true).Length != 0 ||
                   type.GetMethods().Any(m =>
                       m.GetCustomAttributes(typeof(IAllowParentTenantAdministration), true).Length != 0);
        }
    }
}
