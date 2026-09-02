using System.Security.Claims;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.BotServices.Services;

/// <summary>
///     Decides whether a caller may see a job — its status, its artifact, or its deletion — given the
///     tenant that job belongs to (AB#5070).
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>Why this exists at all.</b> The transport tenant gate
///         (<c>TenantAuthorizationMiddleware</c>) reads the <b>route</b> value. The job endpoints of
///         the System API carry no tenant segment, so the gate returns early on them and checks
///         <i>nothing</i> — being unmarked there does not mean "exactly matched", it means
///         "unchecked". Until the System variants are removed (stage 3 of AB#5060) they must therefore
///         perform the very check the middleware would have performed, which is what this guard is.
///     </para>
///     <para>
///         <b>It is a faithful port of the middleware, not a second policy.</b> Same claim
///         (<c>tenant_id</c>), same user/service split (a client-credentials token has no <c>sub</c>),
///         same staging options (<see cref="TenantAuthorizationOptions" />), same allow-list, same
///         fail-closed behaviour. The one thing that differs is where the tenant comes from: the job's
///         own arguments (<see cref="JobTenantBinding" />) instead of the URL. Read the System job
///         routes as "the job's tenant <i>is</i> the route tenant" and every rule lines up.
///     </para>
///     <para>
///         🔴 <b>Service tokens never get the ancestor rule.</b> A client-credentials token's
///         <c>tenant_id</c> proves nothing: mirrored clients share the parent's secret, and a token
///         minted without <c>acr_values</c> falls back to the <b>system</b> tenant, i.e. the root of
///         the hierarchy — so an ancestor rule on that path would hand every service client of the
///         authority every tenant's backup. Exactly as in the middleware, the parent rule is user-token
///         only.
///     </para>
/// </remarks>
public interface IJobTenantAccessGuard
{
    /// <summary>
    ///     Whether <paramref name="user" /> may address a job belonging to
    ///     <paramref name="jobTenantId" /> on a route that carries <b>no</b> tenant segment (the
    ///     deprecated System API). <c>null</c> or empty <paramref name="jobTenantId" /> is always
    ///     refused.
    /// </summary>
    Task<bool> MayAccessJobAsync(ClaimsPrincipal user, string? jobTenantId, string jobId);

    /// <summary>
    ///     Whether a job belonging to <paramref name="jobTenantId" /> is addressable under the route
    ///     tenant <paramref name="routeTenantId" />.
    /// </summary>
    /// <remarks>
    ///     On a tenant route the middleware has already decided whether the caller may address
    ///     <paramref name="routeTenantId" /> — including the parent-tenant administration rule. What is
    ///     left for the endpoint is the ownership question, and that one is an exact match with no
    ///     staging: a parent administering a child addresses the child's route, so it never needs the
    ///     job of a <i>different</i> tenant to be reachable there.
    /// </remarks>
    bool IsJobOfTenant(string routeTenantId, string? jobTenantId, string jobId);
}

/// <inheritdoc />
internal sealed class JobTenantAccessGuard(
    IOptions<TenantAuthorizationOptions> options,
    ILogger<JobTenantAccessGuard> logger,
    ITenantHierarchyReader? tenantHierarchyReader = null)
    : IJobTenantAccessGuard
{
    private const string TenantIdClaimType = "tenant_id";
    private const string ClientIdClaimType = "client_id";

    public bool IsJobOfTenant(string routeTenantId, string? jobTenantId, string jobId)
    {
        if (string.IsNullOrEmpty(jobTenantId))
        {
            LogUnknownTenant(jobId);
            return false;
        }

        if (string.Equals(routeTenantId, jobTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        logger.LogWarning(
            "Denied: job '{JobId}' belongs to tenant '{JobTenantId}' and is therefore not addressable " +
            "under route tenant '{RouteTenantId}' (AB#5070)",
            jobId, jobTenantId, routeTenantId);
        return false;
    }

    public async Task<bool> MayAccessJobAsync(ClaimsPrincipal user, string? jobTenantId, string jobId)
    {
        if (string.IsNullOrEmpty(jobTenantId))
        {
            LogUnknownTenant(jobId);
            return false;
        }

        // Exactly the middleware's split: a client-credentials token carries no user. Both claim
        // types are checked because the JWT handler maps "sub" to NameIdentifier by default, and
        // missing that mapping would silently turn every user token into a service token.
        var isServiceToken = !user.HasClaim(c =>
            c.Type == "sub" || c.Type == ClaimTypes.NameIdentifier);

        return isServiceToken
            ? AllowServiceToken(user, jobTenantId, jobId)
            : await AllowUserTokenAsync(user, jobTenantId, jobId);
    }

    private async Task<bool> AllowUserTokenAsync(ClaimsPrincipal user, string jobTenantId, string jobId)
    {
        var tokenTenantId = user.FindFirstValue(TenantIdClaimType);
        if (!string.IsNullOrEmpty(tokenTenantId) &&
            string.Equals(tokenTenantId, jobTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(tokenTenantId) &&
            await IsChildTenantAsync(tokenTenantId, jobTenantId, jobId))
        {
            // Logged on every grant, at Information, like the middleware's equivalent: the rule
            // widens access and denies nothing new, so the grant log is the only record of who
            // actually uses it. Securing a child tenant includes fetching the file that was secured.
            logger.LogInformation(
                "User token of subject '{Subject}' (client '{ClientId}') was issued for tenant '{TokenTenantId}' " +
                "and accesses job '{JobId}' of its child tenant '{JobTenantId}'; allowed by the parent-tenant " +
                "administration rule (AB#5060, AB#5070)",
                Subject(user), user.FindFirstValue(ClientIdClaimType) ?? "<none>", tokenTenantId, jobId,
                jobTenantId);
            return true;
        }

        if (options.Value.UserTokenEnforcement == UserTokenTenantEnforcementMode.Enforce)
        {
            logger.LogWarning(
                "Denied: user token of subject '{Subject}' (client '{ClientId}') was issued for tenant " +
                "'{TokenTenantId}' but accesses job '{JobId}' of tenant '{JobTenantId}' (AB#5070)",
                Subject(user), user.FindFirstValue(ClientIdClaimType) ?? "<none>",
                string.IsNullOrEmpty(tokenTenantId) ? "<none>" : tokenTenantId, jobId, jobTenantId);
            return false;
        }

        logger.LogWarning(
            "User token of subject '{Subject}' (client '{ClientId}') was issued for tenant '{TokenTenantId}' " +
            "but accesses job '{JobId}' of tenant '{JobTenantId}'. This would be denied with " +
            "UserTokenEnforcement=Enforce (AB#5054, AB#5070)",
            Subject(user), user.FindFirstValue(ClientIdClaimType) ?? "<none>",
            string.IsNullOrEmpty(tokenTenantId) ? "<none>" : tokenTenantId, jobId, jobTenantId);
        return true;
    }

    private bool AllowServiceToken(ClaimsPrincipal user, string jobTenantId, string jobId)
    {
        var settings = options.Value;
        if (settings.ServiceTokenEnforcement == ServiceTokenTenantEnforcementMode.Disabled)
        {
            return true;
        }

        var clientId = user.FindFirstValue(ClientIdClaimType);
        if (settings.IsCrossTenantServiceClient(clientId))
        {
            logger.LogDebug(
                "Service token of client '{ClientId}' is allow-listed for cross-tenant access; job " +
                "'{JobId}' of tenant '{JobTenantId}' not checked",
                clientId, jobId, jobTenantId);
            return true;
        }

        var tokenTenantId = user.FindFirstValue(TenantIdClaimType);
        if (!string.IsNullOrEmpty(tokenTenantId) &&
            string.Equals(tokenTenantId, jobTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (settings.ServiceTokenEnforcement == ServiceTokenTenantEnforcementMode.Enforce)
        {
            logger.LogWarning(
                "Denied: service token of client '{ClientId}' was issued for tenant '{TokenTenantId}' but " +
                "accesses job '{JobId}' of tenant '{JobTenantId}' (AB#5032, AB#5070)",
                clientId, string.IsNullOrEmpty(tokenTenantId) ? "<none>" : tokenTenantId, jobId, jobTenantId);
            return false;
        }

        logger.LogWarning(
            "Service token of client '{ClientId}' was issued for tenant '{TokenTenantId}' but accesses job " +
            "'{JobId}' of tenant '{JobTenantId}'. This would be denied with ServiceTokenEnforcement=Enforce " +
            "(AB#5032, AB#5070)",
            clientId, string.IsNullOrEmpty(tokenTenantId) ? "<none>" : tokenTenantId, jobId, jobTenantId);
        return true;
    }

    /// <summary>
    ///     Whether <paramref name="jobTenantId" /> is a child of <paramref name="tokenTenantId" />.
    ///     Fails closed: a missing reader and an unreadable hierarchy both answer "not related".
    /// </summary>
    private async Task<bool> IsChildTenantAsync(string tokenTenantId, string jobTenantId, string jobId)
    {
        if (tenantHierarchyReader == null)
        {
            logger.LogWarning(
                "No {Reader} is registered, so the parent-tenant administration rule cannot be applied to " +
                "job '{JobId}' of tenant '{JobTenantId}' and the request is judged by the exact tenant " +
                "match (AB#5060, AB#5070)",
                nameof(ITenantHierarchyReader), jobId, jobTenantId);
            return false;
        }

        try
        {
            return await tenantHierarchyReader.IsChildTenantAsync(tokenTenantId, jobTenantId);
        }
        catch (Exception e)
        {
            // The reader is contractually fail-closed, but an artifact endpoint must not depend on
            // that contract holding: an unreadable hierarchy is "not related", never a 500 that a
            // caller could interpret as a transient failure worth retrying into an open door.
            logger.LogError(e,
                "The tenant hierarchy could not be read while judging access to job '{JobId}' of tenant " +
                "'{JobTenantId}'; the parent-tenant administration rule is not applied (AB#5070)",
                jobId, jobTenantId);
            return false;
        }
    }

    private void LogUnknownTenant(string jobId)
    {
        logger.LogWarning(
            "Denied: the tenant of job '{JobId}' could not be determined from its arguments, so the job " +
            "cannot be attributed to a tenant and access is refused (AB#5070)",
            jobId);
    }

    private static string Subject(ClaimsPrincipal user)
    {
        return user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "<none>";
    }
}
