using System.Security.Claims;
using Hangfire.Dashboard;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure;

namespace Meshmakers.Octo.Backend.BotServices;

/// <summary>
///     Scope rules of the Hangfire dashboard at <c>/ui/jobs</c>.
/// </summary>
/// <remarks>
///     AB#5059. The dashboard is the web face of the very jobs
///     <see cref="SystemApi.v1.Controllers.JobsController" /> exposes as an API, and it shows them
///     for <b>every</b> tenant of the instance including their arguments (tenant ids, database
///     names, dump file names). It therefore carries the same scope requirement as that controller
///     rather than a weaker one of its own: read needs
///     <see cref="CommonConstants.OctoApiReadOnly" /> or <see cref="CommonConstants.OctoApiFullAccess" />
///     (mirroring <c>JobApiReadOnlyPolicy</c>), and Hangfire's own mutating commands — Requeue,
///     Delete, trigger a recurring job — need <see cref="CommonConstants.OctoApiFullAccess" />
///     (mirroring <c>JobApiReadWritePolicy</c>).
/// </remarks>
internal static class HangfireDashboardScopes
{
    /// <summary>
    ///     May see the dashboard. Same scope set as <c>BotServiceConstants.JobApiReadOnlyPolicy</c>.
    /// </summary>
    public static bool HasReadAccess(ClaimsPrincipal? user)
    {
        return IsAuthenticated(user) &&
               HasAnyScope(user!, CommonConstants.OctoApiFullAccess, CommonConstants.OctoApiReadOnly);
    }

    /// <summary>
    ///     May use the dashboard's mutating commands. Same scope as
    ///     <c>BotServiceConstants.JobApiReadWritePolicy</c>.
    /// </summary>
    public static bool HasWriteAccess(ClaimsPrincipal? user)
    {
        return IsAuthenticated(user) && HasAnyScope(user!, CommonConstants.OctoApiFullAccess);
    }

    /// <summary>
    ///     AB#4497: the dashboard lists the jobs of every tenant, so only callers of the system tenant may use it.
    ///     The tenant of the caller is the <c>tenant_id</c> claim of the token (the platform convention of
    ///     <c>TenantAuthorizationMiddleware</c>); comparison is case-insensitive like tenant ids elsewhere.
    /// </summary>
    public static bool IsSystemTenantCaller(ClaimsPrincipal? user, string systemTenantId)
    {
        if (user == null || string.IsNullOrWhiteSpace(systemTenantId))
        {
            return false;
        }

        return user.FindAll(TenantIdClaimType).Any(c =>
            string.Equals(c.Value, systemTenantId, StringComparison.OrdinalIgnoreCase));
    }

    private const string TenantIdClaimType = "tenant_id";

    private static bool IsAuthenticated(ClaimsPrincipal? user)
    {
        return user?.Identity is { IsAuthenticated: true };
    }

    /// <summary>
    ///     Accepts both wire encodings of <c>scope</c>: one claim per scope (what the
    ///     <c>RequireClaim</c> policies of this service rely on) and a single space-delimited value.
    ///     This is never more permissive than the policy — it only avoids refusing a correctly
    ///     scoped token over claim splitting.
    /// </summary>
    private static bool HasAnyScope(ClaimsPrincipal user, params string[] acceptedScopes)
    {
        foreach (var claim in user.FindAll(InfrastructureCommon.ClaimScope))
        {
            if (string.IsNullOrWhiteSpace(claim.Value))
            {
                continue;
            }

            foreach (var scope in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries |
                                                        StringSplitOptions.TrimEntries))
            {
                if (acceptedScopes.Contains(scope, StringComparer.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>
///     Authorization filter of the Hangfire dashboard.
/// </summary>
/// <remarks>
///     🔴 AB#5059 — this used to return <c>httpContext.User.Identity?.IsAuthenticated</c> and nothing
///     else. Any principal the service could produce passed it, and the interactive OpenID Connect
///     login this host also configures gives one to <b>every</b> user of <b>every</b> tenant of the
///     identity server. The dashboard then listed the jobs of the whole instance, with arguments, and
///     offered Hangfire's Delete / Requeue commands on them.
///     <para>
///         The filter is deliberately kept as the second gate even though
///         <c>branchedApp.UseAuthorization(JobApiReadOnlyPolicy)</c> already runs in front of it: it
///         is the only check Hangfire itself consults, so it stays correct if the branch's middleware
///         order is ever changed.
///     </para>
/// </remarks>
internal class HangfireDashboardAuthorizationFilter(string systemTenantId) : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();

        return HangfireDashboardScopes.HasReadAccess(httpContext.User) &&
               HangfireDashboardScopes.IsSystemTenantCaller(httpContext.User, systemTenantId);
    }
}

/// <summary>
///     AB#4497: answers <c>403</c> for a dashboard request whose principal is not a caller of the system tenant.
///     Runs after <see cref="HangfireDashboardBearerAuthenticationMiddleware" />, so the principal is the bearer
///     principal carrying <c>tenant_id</c>. The Hangfire filter repeats the check (it is the only one Hangfire
///     itself consults).
/// </summary>
internal class HangfireDashboardSystemTenantMiddleware(RequestDelegate next, string systemTenantId)
{
    public Task Invoke(HttpContext context)
    {
        if (!HangfireDashboardScopes.IsSystemTenantCaller(context.User, systemTenantId))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        return next(context);
    }
}
