using System.Runtime.CompilerServices;
using System.Security.Claims;
using Meshmakers.Octo.Backend.BotServices;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Configuration;

/// <summary>
///     AB#5059 — the Hangfire dashboard at <c>/ui/jobs</c> used to be gated on nothing but
///     "is authenticated": <c>AuthenticatedUserPolicy</c> in front of it and a
///     <c>IDashboardAuthorizationFilter</c> that returned <c>User.Identity?.IsAuthenticated</c>.
///     Because this host also configures an interactive OpenID Connect login, <b>every</b> user of
///     <b>every</b> tenant of the identity server could obtain such a principal and then see the jobs
///     of the whole instance — with their arguments (tenant ids, database names, dump file names) —
///     and use Hangfire's Delete / Requeue commands on them.
///     <para>
///         It now carries the scope requirement of <c>JobsController</c>, the API over the same jobs.
///     </para>
/// </summary>
internal class HangfireDashboardAuthorizationTests
{
    private static ClaimsPrincipal Bearer(params string[] scopes)
    {
        var claims = scopes.Select(s => new Claim(InfrastructureCommon.ClaimScope, s)).ToArray();
        return new ClaimsPrincipal(
            new ClaimsIdentity(claims, JwtBearerDefaults.AuthenticationScheme));
    }

    private static ClaimsPrincipal Anonymous()
    {
        return new ClaimsPrincipal(new ClaimsIdentity());
    }

    [Test]
    public async Task FullAccessScope_MayReadAndWrite()
    {
        var user = Bearer(CommonConstants.OctoApiFullAccess);

        await Assert.That(HangfireDashboardScopes.HasReadAccess(user)).IsTrue();
        await Assert.That(HangfireDashboardScopes.HasWriteAccess(user)).IsTrue();
    }

    /// <summary>
    ///     Read-only tokens keep the dashboard usable for looking at a job, but Hangfire's mutating
    ///     commands are switched off through <c>DashboardOptions.IsReadOnlyFunc</c>. Same split as
    ///     <c>JobApiReadOnlyPolicy</c> vs. <c>JobApiReadWritePolicy</c> on the controller.
    /// </summary>
    [Test]
    public async Task ReadOnlyScope_MayReadButNotWrite()
    {
        var user = Bearer(CommonConstants.OctoApiReadOnly);

        await Assert.That(HangfireDashboardScopes.HasReadAccess(user)).IsTrue();
        await Assert.That(HangfireDashboardScopes.HasWriteAccess(user)).IsFalse();
    }

    /// <summary>
    ///     🔴 The actual hole. A cookie principal from the interactive OIDC login is authenticated and
    ///     carries roles, but never a <c>scope</c> claim — scopes are an access-token property and are
    ///     not returned by the userinfo endpoint. Under the old filter this principal saw everything.
    /// </summary>
    [Test]
    public async Task AuthenticatedWithoutAnyScope_IsRefused()
    {
        var cookieUser = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "any-tenant-user"), new Claim("role", "Reader")], "Cookies"));

        await Assert.That(HangfireDashboardScopes.HasReadAccess(cookieUser)).IsFalse();
        await Assert.That(HangfireDashboardScopes.HasWriteAccess(cookieUser)).IsFalse();
    }

    [Test]
    public async Task ForeignScope_IsRefused()
    {
        var user = Bearer("openid", "profile", "email");

        await Assert.That(HangfireDashboardScopes.HasReadAccess(user)).IsFalse();
        await Assert.That(HangfireDashboardScopes.HasWriteAccess(user)).IsFalse();
    }

    [Test]
    public async Task Anonymous_IsRefused()
    {
        await Assert.That(HangfireDashboardScopes.HasReadAccess(Anonymous())).IsFalse();
        await Assert.That(HangfireDashboardScopes.HasWriteAccess(Anonymous())).IsFalse();
        await Assert.That(HangfireDashboardScopes.HasReadAccess(null)).IsFalse();
        await Assert.That(HangfireDashboardScopes.HasWriteAccess(null)).IsFalse();
    }

    /// <summary>
    ///     Both wire encodings of <c>scope</c>: one claim per scope, and a single space-delimited
    ///     value. Accepting both is never more permissive than the <c>RequireClaim</c> policies — it
    ///     only stops a correctly scoped token from being refused over claim splitting.
    /// </summary>
    [Test]
    public async Task SpaceDelimitedScopeClaim_IsUnderstood()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(InfrastructureCommon.ClaimScope, $"openid profile {CommonConstants.OctoApiFullAccess}")],
            JwtBearerDefaults.AuthenticationScheme));

        await Assert.That(HangfireDashboardScopes.HasReadAccess(user)).IsTrue();
        await Assert.That(HangfireDashboardScopes.HasWriteAccess(user)).IsTrue();
    }

    /// <summary>
    ///     A scope that merely *contains* the accepted one must not pass — the check is on whole,
    ///     space-separated tokens.
    /// </summary>
    [Test]
    public async Task PrefixLikeScope_IsRefused()
    {
        var user = Bearer("octo_api.read_only.something", "not_octo_api");

        await Assert.That(HangfireDashboardScopes.HasReadAccess(user)).IsFalse();
    }

    /// <summary>
    ///     The composed pipeline lives in the top-level statements of <c>Program.cs</c> and cannot be
    ///     resolved from a unit test, so the branch's wiring is pinned at the source — the technique
    ///     <see cref="TenantAuthorizationWiringTests" /> already uses in this project. Every element
    ///     here fails silently if removed.
    /// </summary>
    [Test]
    public async Task Program_GatesTheDashboardBranchOnTheJobApiScope()
    {
        var program = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(),
            "src", "BotServices", "Program.cs"));

        // The bearer token behind ?jwt_token= has to become the principal — app.UseAuthentication()
        // only runs the default (Cookies) scheme.
        await Assert.That(program)
            .Contains("UseMiddleware<HangfireDashboardBearerAuthenticationMiddleware>()");
        // ... and the branch requires the job API scope, not merely an authenticated user.
        await Assert.That(program)
            .Contains("branchedApp.UseAuthorization(BotServiceConstants.JobApiReadOnlyPolicy)");
        await Assert.That(program).DoesNotContain(
            "branchedApp.UseAuthorization(BotServiceConstants.AuthenticatedUserPolicy)");
        // Hangfire's own commands need the write scope.
        await Assert.That(program).Contains("IsReadOnlyFunc");
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
    }
}
