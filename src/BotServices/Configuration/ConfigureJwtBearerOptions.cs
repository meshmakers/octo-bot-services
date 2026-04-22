using Meshmakers.Common.Shared;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.BotServices.Configuration;

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

        // Explicitly set the valid issuer so token validation does not depend on fetching
        // the OIDC discovery document. This prevents IDX10204 errors when the identity
        // service is temporarily unreachable (e.g. during rolling updates).
        options.TokenValidationParameters.ValidIssuer = authorityUrl;
    }
}