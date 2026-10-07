using System.Security.Claims;
using Duende.IdentityModel;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.AspNetCore.Authorization;

namespace Meshmakers.Octo.Backend.BotServices.Configuration;

/// <summary>
///     The authorization policies of the bot service, in one place so the host and the API tests use the
///     very same definitions.
/// </summary>
internal static class BotAuthorizationPolicies
{
    /// <summary>
    ///     Registers the policies.
    /// </summary>
    /// <remarks>
    ///     Roles are tenant roles the identity service issues as one <c>role</c> claim per role name. The JWT
    ///     bearer handler keeps its ASP.NET default <c>MapInboundClaims = true</c> and renames those claims to
    ///     <see cref="ClaimTypes.Role" /> before the principal is built, while the identity's
    ///     <c>RoleClaimType</c> is <c>role</c> (<c>ConfigureJwtBearerOptions</c>) — so <c>RequireRole</c> /
    ///     <c>IsInRole</c> silently answers <c>false</c> for every real token (AB#5539). The role policies
    ///     therefore probe both spellings, the platform convention of the communication controller
    ///     (<c>PrincipalRoleExtensions.HasRole</c>), the MCP service and the asset repository. A token without
    ///     the role gets <c>403</c>. Scopes are not affected: <c>scope</c> is not in the inbound claim map, and
    ///     the identity service emits one <c>scope</c> claim per value. The tenant itself is checked by the
    ///     transport tenant gate on the route.
    /// </remarks>
    public static void AddBotPolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(BotServiceConstants.AuthenticatedUserPolicy,
            policyBuilder => policyBuilder.RequireAuthenticatedUser());

        options.AddPolicy(BotServiceConstants.JobApiReadOnlyPolicy, policyBuilder =>
            policyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                CommonConstants.OctoApiFullAccess,
                CommonConstants.OctoApiReadOnly));

        options.AddPolicy(BotServiceConstants.JobApiReadWritePolicy, policyBuilder =>
            policyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                CommonConstants.OctoApiFullAccess));

        // AB#5544 (contract §6): trigger sweeps and delete dumps.
        options.AddPolicy(BotServiceConstants.SecretManagementPolicy, policyBuilder =>
            policyBuilder.RequireClaim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess)
                .RequireAssertion(context => HasTenantRole(context.User, CommonConstants.SecretManagementRole)));

        // AB#5544 (contract §6): sweep report and run list.
        options.AddPolicy(BotServiceConstants.SecretAdministrationReadPolicy, policyBuilder =>
            policyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                    CommonConstants.OctoApiFullAccess,
                    CommonConstants.OctoApiReadOnly)
                .RequireAssertion(context => HasTenantRole(context.User, CommonConstants.AdminPanelManagementRole)));
    }

    /// <summary>
    ///     Whether the principal carries the tenant role under the identity's role claim type, the raw JWT
    ///     <c>role</c> claim or the inbound-mapped <see cref="ClaimTypes.Role" /> claim.
    /// </summary>
    internal static bool HasTenantRole(ClaimsPrincipal principal, string roleName)
    {
        return principal.IsInRole(roleName) ||
               principal.Claims.Any(c =>
                   (c.Type == JwtClaimTypes.Role || c.Type == ClaimTypes.Role) &&
                   string.Equals(c.Value, roleName, StringComparison.Ordinal));
    }
}
