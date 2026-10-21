using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Meshmakers.Octo.Backend.BotServices;
using Meshmakers.Octo.Backend.BotServices.Configuration;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TenantJobsController = Meshmakers.Octo.Backend.BotServices.TenantApi.v1.Controllers.JobsController;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Configuration;

/// <summary>
///     AB#4497 — wiring of the role gates as <c>Program.cs</c> builds them: the production policy registration,
///     the production tenant-gate defaults, the policy attached to every entry point, the tus upload rule and the
///     system-tenant rule of the Hangfire dashboard.
/// </summary>
/// <remarks>
///     The real <c>Program.cs</c> consists of top-level statements that need Mongo and a broker, so it cannot be
///     hosted in a unit test. The tests therefore use the very registrations it calls
///     (<c>AddBotPolicies</c>, <c>AddOctoTenantAuthorization(IConfiguration)</c>) and pin the call sites in the
///     source text, the convention of <see cref="TenantAuthorizationWiringTests" />.
/// </remarks>
internal class TenantJobRoleWiringTests
{
    private static ClaimsPrincipal Caller(string tenantId, string? scope, params string[] roleClaimTypesAndValues)
    {
        var claims = new List<Claim> { new("tenant_id", tenantId), new("sub", "s") };
        if (scope != null)
        {
            claims.Add(new Claim(InfrastructureCommon.ClaimScope, scope));
        }

        for (var i = 0; i < roleClaimTypesAndValues.Length; i += 2)
        {
            claims.Add(new Claim(roleClaimTypesAndValues[i], roleClaimTypesAndValues[i + 1]));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer", "name", "role"));
    }

    private static async Task<bool> PolicyAllows(ClaimsPrincipal user)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(o => o.AddBotPolicies());
        await using var provider = services.BuildServiceProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var result = await authorization.AuthorizeAsync(user, BotServiceConstants.TenantJobManagementPolicy);
        return result.Succeeded;
    }

    [Test]
    public async Task Policy_ScopeAndRole_Succeeds()
    {
        await Assert.That(await PolicyAllows(Caller("t", CommonConstants.OctoApiFullAccess,
            ClaimTypes.Role, CommonConstants.TenantManagementRole))).IsTrue();
    }

    [Test]
    public async Task Policy_RawRoleClaimSpelling_Succeeds()
    {
        await Assert.That(await PolicyAllows(Caller("t", CommonConstants.OctoApiFullAccess,
            "role", CommonConstants.TenantManagementRole))).IsTrue();
    }

    [Test]
    public async Task Policy_ScopeWithoutRole_IsRefused()
    {
        await Assert.That(await PolicyAllows(Caller("t", CommonConstants.OctoApiFullAccess))).IsFalse();
    }

    [Test]
    public async Task Policy_RoleWithoutScope_IsRefused()
    {
        await Assert.That(await PolicyAllows(Caller("t", null,
            ClaimTypes.Role, CommonConstants.TenantManagementRole))).IsFalse();
    }

    [Test]
    public async Task Policy_ReadOnlyScopeWithRole_IsRefused()
    {
        await Assert.That(await PolicyAllows(Caller("t", CommonConstants.OctoApiReadOnly,
            ClaimTypes.Role, CommonConstants.TenantManagementRole))).IsFalse();
    }

    /// <summary>Every entry point of the tenant routes carries the policy (AB#6848 added the fixup run).</summary>
    [Test]
    [Arguments(nameof(TenantJobsController.RunFixupScripts))]
    [Arguments(nameof(TenantJobsController.DumpRepository))]
    [Arguments(nameof(TenantJobsController.RestoreFromUpload))]
    [Arguments(nameof(TenantJobsController.ExportArchiveData))]
    [Arguments(nameof(TenantJobsController.ImportArchiveDataFromUpload))]
    [Arguments(nameof(TenantJobsController.DownloadJobResult))]
    public async Task TenantJobEntryPoint_CarriesTheRolePolicy(string method)
    {
        var attribute = typeof(TenantJobsController).GetMethod(method)!.GetCustomAttribute<AuthorizeAttribute>();

        await Assert.That(attribute?.Policy).IsEqualTo(BotServiceConstants.TenantJobManagementPolicy);
    }

    [Test]
    public async Task SystemDownloadRoute_CarriesTheRolePolicy()
    {
        var method = typeof(Meshmakers.Octo.Backend.BotServices.SystemApi.v1.Controllers.JobsController)
            .GetMethod("DownloadExportRtResult")!;

        await Assert.That(method.GetCustomAttribute<AuthorizeAttribute>()?.Policy)
            .IsEqualTo(BotServiceConstants.TenantJobManagementPolicy);
    }

    /// <summary>The production defaults of the tenant gate (no override in Program.cs) are Enforce.</summary>
    [Test]
    public async Task ProductionTenantGateDefaults_AreEnforce()
    {
        var services = new ServiceCollection();
        services.AddOctoTenantAuthorization(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TenantAuthorizationOptions>>().Value;

        await Assert.That(options.UserTokenEnforcement).IsEqualTo(UserTokenTenantEnforcementMode.Enforce);
        await Assert.That(options.ServiceTokenEnforcement).IsEqualTo(ServiceTokenTenantEnforcementMode.Enforce);
    }

    [Test]
    public async Task ProgramCs_WiresPoliciesTusRuleAndDashboardRule()
    {
        var program = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "src", "BotServices", "Program.cs"));
        var code = Regex.Replace(program, @"//.*?$", string.Empty, RegexOptions.Multiline);

        await Assert.That(code).Contains("AddOctoTenantAuthorization(builder.Configuration)");
        await Assert.That(code).Contains("options.AddBotPolicies()");
        await Assert.That(code).Contains("UseOctoTenantAuthorization()");
        await Assert.That(code).DoesNotContain("UserTokenEnforcement");
        await Assert.That(code).DoesNotContain("ServiceTokenEnforcement");
        await Assert.That(code).Contains("BotServiceConstants.TenantJobManagementPolicy");
        await Assert.That(code).Contains("TusUploadAuthorization.MayUpload(");
        await Assert.That(code).Contains("HangfireDashboardAccessMiddleware>(dashboardSystemTenantId)");
        await Assert.That(code).Contains("new HangfireDashboardAuthorizationFilter(dashboardSystemTenantId)");
    }

    // tus upload

    [Test]
    public async Task Tus_ScopeAndRole_MayUpload()
    {
        await Assert.That(TusUploadAuthorization.MayUpload(Caller("t", CommonConstants.OctoApiFullAccess,
            ClaimTypes.Role, CommonConstants.TenantManagementRole))).IsTrue();
    }

    [Test]
    public async Task Tus_ScopeWithoutRole_MayNotUpload()
    {
        await Assert.That(TusUploadAuthorization.MayUpload(Caller("t", CommonConstants.OctoApiFullAccess,
            ClaimTypes.Role, CommonConstants.AdminPanelManagementRole))).IsFalse();
    }

    [Test]
    public async Task Tus_RoleWithoutScope_MayNotUpload()
    {
        await Assert.That(TusUploadAuthorization.MayUpload(Caller("t", CommonConstants.OctoApiReadOnly,
            ClaimTypes.Role, CommonConstants.TenantManagementRole))).IsFalse();
    }

    [Test]
    public async Task Tus_AnonymousAndNull_MayNotUpload()
    {
        await Assert.That(TusUploadAuthorization.MayUpload(new ClaimsPrincipal(new ClaimsIdentity()))).IsFalse();
        await Assert.That(TusUploadAuthorization.MayUpload(null)).IsFalse();
    }

    // Hangfire dashboard

    private const string SystemTenant = "octosystem";

    [Test]
    public async Task Dashboard_SystemTenantCaller_IsAccepted_CaseInsensitive()
    {
        await Assert.That(HangfireDashboardScopes.IsSystemTenantCaller(Caller("OctoSystem", null), SystemTenant))
            .IsTrue();
    }

    [Test]
    public async Task Dashboard_TenantACaller_IsRefused()
    {
        await Assert.That(HangfireDashboardScopes.IsSystemTenantCaller(Caller("tenant-a", null), SystemTenant))
            .IsFalse();
    }

    [Test]
    public async Task Dashboard_CallerWithoutTenantClaimOrBlankSystemTenant_IsRefused()
    {
        var noTenant = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "s")], "Bearer"));

        await Assert.That(HangfireDashboardScopes.IsSystemTenantCaller(noTenant, SystemTenant)).IsFalse();
        await Assert.That(HangfireDashboardScopes.IsSystemTenantCaller(Caller("", null), "")).IsFalse();
        await Assert.That(HangfireDashboardScopes.IsSystemTenantCaller(null, SystemTenant)).IsFalse();
    }

    [Test]
    public async Task DashboardMiddleware_TenantAToken_Gets403_AndDashboardIsNotReached()
    {
        var reached = false;
        var middleware = new HangfireDashboardAccessMiddleware(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        }, SystemTenant);
        var context = new DefaultHttpContext
        {
            User = Caller("tenant-a", CommonConstants.OctoApiFullAccess)
        };

        await middleware.Invoke(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status403Forbidden);
        await Assert.That(reached).IsFalse();
    }

    [Test]
    public async Task DashboardMiddleware_SystemTenantToken_ReachesTheDashboard()
    {
        var reached = false;
        var middleware = new HangfireDashboardAccessMiddleware(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        }, SystemTenant);
        var context = new DefaultHttpContext
        {
            User = Caller(SystemTenant, CommonConstants.OctoApiFullAccess, ClaimTypes.Role, CommonConstants.TenantManagementRole)
        };

        await middleware.Invoke(context);

        await Assert.That(reached).IsTrue();
        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status200OK);
    }


    [Test]
    public async Task DashboardMiddleware_SystemTenantTokenWithoutTenantManagement_Gets403()
    {
        var reached = false;
        var middleware = new HangfireDashboardAccessMiddleware(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        }, SystemTenant);
        var context = new DefaultHttpContext
        {
            User = Caller(SystemTenant, CommonConstants.OctoApiFullAccess,
                ClaimTypes.Role, CommonConstants.AdminPanelManagementRole)
        };

        await middleware.Invoke(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status403Forbidden);
        await Assert.That(reached).IsFalse();
    }

    [Test]
    public async Task DashboardMiddleware_TenantATokenWithTenantManagement_Gets403()
    {
        var reached = false;
        var middleware = new HangfireDashboardAccessMiddleware(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        }, SystemTenant);
        var context = new DefaultHttpContext
        {
            User = Caller("tenant-a", CommonConstants.OctoApiFullAccess, ClaimTypes.Role, CommonConstants.TenantManagementRole)
        };

        await middleware.Invoke(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status403Forbidden);
        await Assert.That(reached).IsFalse();
    }

    [Test]
    public async Task Dashboard_SystemTenantAdministrator_RequiresTenantAndRole_BothRoleSpellings()
    {
        await Assert.That(HangfireDashboardScopes.IsSystemTenantAdministrator(
            Caller(SystemTenant, null, ClaimTypes.Role, CommonConstants.TenantManagementRole), SystemTenant)).IsTrue();
        await Assert.That(HangfireDashboardScopes.IsSystemTenantAdministrator(
            Caller(SystemTenant, null, "role", CommonConstants.TenantManagementRole), SystemTenant)).IsTrue();
        await Assert.That(HangfireDashboardScopes.IsSystemTenantAdministrator(
            Caller(SystemTenant, null), SystemTenant)).IsFalse();
        await Assert.That(HangfireDashboardScopes.IsSystemTenantAdministrator(
            Caller("tenant-a", null, ClaimTypes.Role, CommonConstants.TenantManagementRole), SystemTenant)).IsFalse();
        await Assert.That(HangfireDashboardScopes.IsSystemTenantAdministrator(null, SystemTenant)).IsFalse();
    }

    [Test]
    public async Task DashboardFilter_RequiresScopeSystemTenantAndTenantManagement()
    {
        var filter = new HangfireDashboardAuthorizationFilter(SystemTenant);

        await Assert.That(Allows(filter, Caller(SystemTenant, CommonConstants.OctoApiFullAccess, ClaimTypes.Role, CommonConstants.TenantManagementRole))).IsTrue();
        await Assert.That(Allows(filter, Caller(SystemTenant, CommonConstants.OctoApiReadOnly, ClaimTypes.Role, CommonConstants.TenantManagementRole))).IsTrue();
        // role missing
        await Assert.That(Allows(filter, Caller(SystemTenant, CommonConstants.OctoApiFullAccess))).IsFalse();
        // other tenant
        await Assert.That(Allows(filter, Caller("tenant-a", CommonConstants.OctoApiFullAccess, ClaimTypes.Role, CommonConstants.TenantManagementRole))).IsFalse();
        // scope missing
        await Assert.That(Allows(filter, Caller(SystemTenant, null, ClaimTypes.Role, CommonConstants.TenantManagementRole))).IsFalse();
    }

    private static bool Allows(HangfireDashboardAuthorizationFilter filter, ClaimsPrincipal user)
    {
        var httpContext = new DefaultHttpContext
        {
            User = user,
            RequestServices = new ServiceCollection().BuildServiceProvider()
        };
        var context = new Hangfire.Dashboard.AspNetCoreDashboardContext(
            NSubstitute.Substitute.For<Hangfire.JobStorage>(), new Hangfire.DashboardOptions(), httpContext);
        return filter.Authorize(context);
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
    }
}
