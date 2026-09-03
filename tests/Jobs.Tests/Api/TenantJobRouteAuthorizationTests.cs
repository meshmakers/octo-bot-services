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
    ///     The tenant the job runs against comes from the <b>route segment</b> — the value the
    ///     transport gate checked before the request ever reached the controller.
    /// </summary>
    /// <remarks>
    ///     This replaces the migration invariant it grew out of. Until stage 3 of AB#5060 the same
    ///     operation existed twice, and the test asserted that both surfaces enqueued an identical
    ///     Hangfire job — same type, same method, same arguments — which is what made the deprecated
    ///     System variant safe to keep as a fallback. That variant is gone, so there is no second
    ///     surface left to compare against, and what remains worth pinning is the half that carries
    ///     the security property: a job started through <c>{tenantId}/v1/jobs/…</c> acts on the tenant
    ///     the gate authorized, and on no other. The removed surface took its tenant from
    ///     <c>?tenantId=</c>, so gate and job could disagree; here they cannot.
    /// </remarks>
    [Test]
    [Arguments("run-fixup-scripts", "")]
    [Arguments("dump-repository", "")]
    [Arguments("export-archive-data", ExportQuery)]
    [Arguments("restore-from-upload", RestoreQuery)]
    [Arguments("import-archive-data-from-upload", ImportQuery)]
    public async Task TenantRoute_EnqueuesTheJobForTheRouteTenant(string route, string query)
    {
        using var host = await JobsApiTestHost.StartAsync();
        host.ResetJobClient();

        var response = await host.PostAsync($"/{Child}/v1/jobs/{route}{query}",
            JobsApiTestHost.UserToken(Child));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        await Assert.That(host.EnqueuedJobCount).IsEqualTo(1);

        var enqueued = host.LastEnqueuedJob();
        var carriesRouteTenant = enqueued.Args.Any(a => string.Equals(a?.ToString(), Child, StringComparison.Ordinal));
        await Assert.That(carriesRouteTenant).IsTrue();
    }

    /// <summary>
    ///     The five <i>enqueueing</i> operations are gone from the System API (stage 3 of AB#5060).
    ///     Until then they took their tenant from <c>?tenantId=</c>, which the transport gate cannot
    ///     see, so any authenticated caller could start a job against any tenant — the hole the tenant
    ///     routes were built to close. This asserts that no such request enqueues anything any more,
    ///     for the caller that used to be able to do it: a token issued for an unrelated tenant.
    /// </summary>
    /// <remarks>
    ///     🔴 <b>The refusal is 403, not 404, and that is worth knowing.</b> Once the System actions
    ///     were removed, <c>system/v1/jobs/dump-repository</c> stopped matching a route of its own and
    ///     started matching the tenant route <c>{tenantId:tenantId}/v1/jobs/dump-repository</c> with
    ///     <c>tenantId = "system"</c> — so the request now reaches the gate, which refuses it because
    ///     the caller's token names a different tenant. The outcome an external caller sees is a
    ///     refusal either way, and going through the gate is the stricter of the two paths. What it
    ///     costs is that a tenant literally named <c>system</c> would make the old URLs live again as
    ///     that tenant's routes; the tenant-id route constraint is what keeps this from being reachable
    ///     for anything but a syntactically valid tenant id.
    ///     <para>
    ///         Enqueueing only. The System <i>artifact</i> routes (<c>GET</c>, <c>download</c>,
    ///         <c>DELETE</c>) deliberately stay: they address a Hangfire job id, which is global to the
    ///         instance, and AB#5070 gave them the check in code that the middleware cannot perform on
    ///         a route with no tenant segment. See <see cref="JobArtifactTenantBindingTests" />.
    ///     </para>
    /// </remarks>
    [Test]
    [Arguments("run-fixup-scripts", "")]
    [Arguments("dump-repository", "")]
    [Arguments("export-archive-data", ExportQuery)]
    [Arguments("restore-from-upload", RestoreQuery)]
    [Arguments("import-archive-data-from-upload", ImportQuery)]
    public async Task SystemRoute_IsGone_AndEnqueuesNothing(string route, string query)
    {
        using var host = await JobsApiTestHost.StartAsync();
        host.ResetJobClient();

        var separator = query.Length == 0 ? "?" : "&";
        var response = await host.PostAsync($"/system/v1/jobs/{route}{query}{separator}tenantId={Child}",
            JobsApiTestHost.UserToken(Unrelated));

        await Assert.That(response.IsSuccessStatusCode).IsFalse();
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(0);
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
