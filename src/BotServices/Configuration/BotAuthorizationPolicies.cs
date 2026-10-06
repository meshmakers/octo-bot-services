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
    ///     Roles are tenant roles carried as <c>role</c> claims of the bearer token
    ///     (<c>RoleClaimType = role</c>, see <c>ConfigureJwtBearerOptions</c>); a token without the role gets
    ///     <c>403</c>. The tenant itself is checked by the transport tenant gate on the route.
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
                .RequireRole(CommonConstants.SecretManagementRole));

        // AB#5544 (contract §6): sweep report and run list.
        options.AddPolicy(BotServiceConstants.SecretAdministrationReadPolicy, policyBuilder =>
            policyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                    CommonConstants.OctoApiFullAccess,
                    CommonConstants.OctoApiReadOnly)
                .RequireRole(CommonConstants.AdminPanelManagementRole));
    }
}
