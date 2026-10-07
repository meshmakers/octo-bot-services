using System.Security.Claims;
using System.Security.Cryptography;
using Meshmakers.Octo.Backend.BotServices;
using Meshmakers.Octo.Backend.BotServices.Configuration;
using Meshmakers.Octo.Communication.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Configuration;

/// <summary>
///     AB#5539 — the secret admin policies against a principal built by the real bearer token handler from a
///     token in the shape the identity service issues (one <c>role</c> claim per role, one <c>scope</c> claim per
///     scope value, <c>tenant_id</c>, <c>sub</c>), validated with the options of
///     <see cref="ConfigureJwtBearerOptions" /> and the handler's default inbound claim mapping. Before the fix,
///     <c>RequireRole</c> answered <c>false</c> for every such token, so the sweep run list returned 403 to an
///     <c>AdminPanelManagement</c> user.
/// </summary>
internal class SecretPolicyTokenClaimTests
{
    private const string Authority = "https://localhost:5003/";

    private static async Task<ClaimsPrincipal> ValidateAsync(IReadOnlyList<string> roles, IReadOnlyList<string> scopes)
    {
        var signingKey = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));

        var options = new JwtBearerOptions();
        new ConfigureJwtBearerOptions(Options.Create(new OctoBotServicesOptions { AuthorityUrl = Authority }))
            .Configure(options);
        var parameters = options.TokenValidationParameters.Clone();
        parameters.IssuerSigningKey = signingKey;
        parameters.ValidAudience = CommonConstants.OctoApi;

        // Wire shape of the identity service (OctoTokenClaimsService / OctoAccessTokenShapeHandler): roles and
        // scopes as JSON arrays (one claim per value), a single value as a plain string.
        var claims = new Dictionary<string, object>
        {
            ["sub"] = "test-subject",
            ["name"] = "test-user",
            ["client_id"] = CommonConstants.BotServicesClientId,
            ["tenant_id"] = "child",
            ["role"] = roles.Count == 1 ? roles[0] : roles.ToArray(),
            ["scope"] = scopes.Count == 1 ? scopes[0] : scopes.ToArray()
        };
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Authority,
            Audience = CommonConstants.OctoApi,
            Claims = claims,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256)
        });

        var handler = options.TokenHandlers.Single();
        var result = await handler.ValidateTokenAsync(token, parameters);
        await Assert.That(result.IsValid).IsTrue();
        return new ClaimsPrincipal(result.ClaimsIdentity);
    }

    private static async Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string policy)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(o => o.AddBotPolicies());
        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(principal, policy);
        return result.Succeeded;
    }

    [Test]
    public async Task RealHandler_RenamesRoleClaims_SoPlainIsInRoleWouldMiss()
    {
        // Pins the trap the fix exists for: if this ever stops holding, the probe is merely redundant.
        var principal = await ValidateAsync([CommonConstants.AdminPanelManagementRole], [CommonConstants.OctoApiFullAccess]);

        await Assert.That(principal.HasClaim(ClaimTypes.Role, CommonConstants.AdminPanelManagementRole)).IsTrue();
        await Assert.That(principal.IsInRole(CommonConstants.AdminPanelManagementRole)).IsFalse();
    }

    [Test]
    [Arguments("octo_api")]
    [Arguments("octo_api.read_only")]
    public async Task SweepRunList_AdminPanelManagement_IsAuthorized(string scope)
    {
        var principal = await ValidateAsync([CommonConstants.AdminPanelManagementRole], ["openid", "profile", scope]);

        await Assert.That(await AuthorizeAsync(principal, BotServiceConstants.SecretAdministrationReadPolicy)).IsTrue();
    }

    [Test]
    public async Task SweepRunList_WithoutAdminPanelManagement_IsDenied()
    {
        var principal = await ValidateAsync([CommonConstants.SecretManagementRole], [CommonConstants.OctoApiFullAccess]);

        await Assert.That(await AuthorizeAsync(principal, BotServiceConstants.SecretAdministrationReadPolicy)).IsFalse();
    }

    [Test]
    public async Task SweepRunList_WithoutApiScope_IsDenied()
    {
        var principal = await ValidateAsync([CommonConstants.AdminPanelManagementRole], ["openid", "profile"]);

        await Assert.That(await AuthorizeAsync(principal, BotServiceConstants.SecretAdministrationReadPolicy)).IsFalse();
    }

    [Test]
    public async Task SecretManagement_WithRoleAndFullScope_IsAuthorized()
    {
        var principal = await ValidateAsync(
            [CommonConstants.AdminPanelManagementRole, CommonConstants.SecretManagementRole],
            [CommonConstants.OctoApiFullAccess]);

        await Assert.That(await AuthorizeAsync(principal, BotServiceConstants.SecretManagementPolicy)).IsTrue();
    }

    [Test]
    public async Task SecretManagement_ReadOnlyScope_IsDenied()
    {
        var principal = await ValidateAsync([CommonConstants.SecretManagementRole], [CommonConstants.OctoApiReadOnly]);

        await Assert.That(await AuthorizeAsync(principal, BotServiceConstants.SecretManagementPolicy)).IsFalse();
    }

    [Test]
    public async Task SecretManagement_AdminPanelManagementOnly_IsDenied()
    {
        var principal = await ValidateAsync([CommonConstants.AdminPanelManagementRole], [CommonConstants.OctoApiFullAccess]);

        await Assert.That(await AuthorizeAsync(principal, BotServiceConstants.SecretManagementPolicy)).IsFalse();
    }
}
