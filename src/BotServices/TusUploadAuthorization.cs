using System.Security.Claims;
using Meshmakers.Octo.Backend.BotServices.Configuration;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure;

namespace Meshmakers.Octo.Backend.BotServices;

/// <summary>
///     Decides whether a principal may use the tus upload endpoint (<c>{tenantId}/v1/tus-upload</c>).
/// </summary>
/// <remarks>
///     AB#4497. The endpoint only feeds the restore and the archive import, both of which need the tenant role
///     <c>TenantManagement</c> (<see cref="BotServiceConstants.TenantJobManagementPolicy" />). Uploading a file
///     is gated by the same rule, so a caller without the role cannot stage files (disk space, planted restore
///     input) that the consuming job would refuse anyway. The tus middleware is not an MVC endpoint, so the
///     policy cannot be attached with <c>[Authorize]</c>; the rule lives here and is called from
///     <c>OnAuthorizeAsync</c>.
/// </remarks>
internal static class TusUploadAuthorization
{
    /// <summary>
    ///     <c>true</c> for an authenticated principal with the full-access scope and the tenant role
    ///     <c>TenantManagement</c>.
    /// </summary>
    public static bool MayUpload(ClaimsPrincipal? user)
    {
        return user?.Identity is { IsAuthenticated: true } &&
               user.HasClaim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess) &&
               BotAuthorizationPolicies.HasTenantRole(user, CommonConstants.TenantManagementRole);
    }
}
