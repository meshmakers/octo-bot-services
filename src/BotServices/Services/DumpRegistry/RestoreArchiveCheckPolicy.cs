using System.Security.Claims;
using Meshmakers.Octo.Backend.Jobs.DumpRegistry;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.BotServices.Services.DumpRegistry;

/// <summary>
///     Decides the archive check of a restore from its caller: a system operator (a token of the system tenant with
///     <c>TenantManagement</c>) restores any archive; every other caller, also an administrator of the target tenant or
///     of a parent tenant, restores only a dump registered for the target tenant. Unknown callers get the check.
/// </summary>
public sealed class RestoreArchiveCheckPolicy(IOptions<OctoSystemConfiguration> systemConfiguration)
{
    /// <summary>The check for a REST caller.</summary>
    public ArchiveRegistrationCheck For(ClaimsPrincipal? caller)
    {
        var systemTenantId = systemConfiguration.Value.SystemTenantId;
        var isSystemOperator = !string.IsNullOrWhiteSpace(systemTenantId) &&
                               HangfireDashboardScopes.IsSystemTenantAdministrator(caller, systemTenantId);
        return isSystemOperator ? ArchiveRegistrationCheck.NotRequired : ArchiveRegistrationCheck.Required;
    }

    /// <summary>The check for the caller of a CK method; the tenant is the one the token was issued for.</summary>
    public ArchiveRegistrationCheck For(CkMethodPrincipal? caller)
    {
        var systemTenantId = systemConfiguration.Value.SystemTenantId;
        if (caller?.TokenTenantId == null || string.IsNullOrWhiteSpace(systemTenantId))
        {
            return ArchiveRegistrationCheck.Required;
        }

        var isSystemTenantToken =
            string.Equals(caller.TokenTenantId, systemTenantId, StringComparison.OrdinalIgnoreCase);
        var hasTenantManagement = caller.Roles.Any(role =>
            string.Equals(role, CommonConstants.TenantManagementRole, StringComparison.OrdinalIgnoreCase));
        return isSystemTenantToken && hasTenantManagement
            ? ArchiveRegistrationCheck.NotRequired
            : ArchiveRegistrationCheck.Required;
    }
}
