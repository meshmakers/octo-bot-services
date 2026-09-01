using IdentityModel;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Communication.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.BotServices.Configuration;

/// <summary>
///     Configures the JWT bearer scheme this service authenticates its API callers with.
/// </summary>
/// <remarks>
///     🔴 <b>This is the only configurator of the bearer scheme. Keep it that way (AB#5054).</b>
///     <c>Program.cs</c> used to add a second one through <c>AddJwtBearer(jwt =&gt; …)</c> that
///     assigned a brand-new <c>TokenValidationParameters</c>. The options factory runs configurators
///     in registration order, so that assignment ran last and silently discarded everything set
///     here — the explicit <c>ValidIssuer</c>, and the <c>AuthenticationType</c> below that a
///     security gate depends on. Nothing about that failure is visible: it compiles, and a unit test
///     of this class in isolation still passes. octo-ai-services shipped a full release in exactly
///     that state (AB#5051 → AB#5056). The OpenID Connect scheme in <c>Program.cs</c> is a different
///     options type and is unaffected.
/// </remarks>
internal class ConfigureJwtBearerOptions(IOptions<OctoBotServicesOptions> botServicesOptions)
    : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(JwtBearerOptions options)
    {
        Configure(Options.DefaultName, options);
    }

    public void Configure(string? name, JwtBearerOptions options)
    {
        var authorityUrl = botServicesOptions.Value.AuthorityUrl.EnsureEndsWith("/");
        options.Authority = authorityUrl;
        options.Audience = CommonConstants.OctoApi;

        options.TokenValidationParameters.NameClaimType = JwtClaimTypes.Name;
        options.TokenValidationParameters.RoleClaimType = JwtClaimTypes.Role;

        // Explicitly set the valid issuer so token validation does not depend on fetching
        // the OIDC discovery document. This prevents IDX10204 errors when the identity
        // service is temporarily unreachable (e.g. during rolling updates).
        options.TokenValidationParameters.ValidIssuer = authorityUrl;

        // AB#5054 — label the authenticated identity "Bearer" so TenantAuthorizationMiddleware
        // (UseOctoTenantAuthorization(), AB#5032/AB#5047) actually runs its checks instead of
        // returning early on every bearer request. The middleware deliberately skips principals
        // whose AuthenticationType is not "Bearer" to avoid false 403s on the cookie/OIDC
        // principals this service also issues — and the JWT handler's default label is
        // "AuthenticationTypes.Federation", not "Bearer". Only this scheme is relabelled; the
        // cookie principal keeps its own AuthenticationType and is still skipped.
        //
        // This service has no {tenantId} route segment at all (every controller is routed
        // `system/v{version}/[controller]`), so the middleware still returns early on the missing
        // route tenant and nothing changes today. The label is set anyway so the gate is not a
        // no-op waiting to surprise whoever adds the first tenant-scoped route here — the exact
        // failure mode AB#5054 exists to remove. Same fix as octo-mcp-service (AB#4315) and
        // octo-ai-services (AB#5051/AB#5056).
        options.TokenValidationParameters.AuthenticationType = JwtBearerDefaults.AuthenticationScheme;
    }
}
