using Meshmakers.Common.Shared;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.BotServices.Configuration;

internal class ConfigureIdentityServerAuthenticationOptions : IConfigureNamedOptions<JwtBearerOptions>
{
    private readonly IOptions<OctoBotServicesOptions> _botServicesOptions;

    public ConfigureIdentityServerAuthenticationOptions(IOptions<OctoBotServicesOptions> botServicesOptions)
    {
        _botServicesOptions = botServicesOptions;
    }

    public void Configure(JwtBearerOptions options)
    {
        Configure(Options.DefaultName, options);
    }

    public void Configure(string? name, JwtBearerOptions options)
    {
        options.Authority = _botServicesOptions.Value.AuthorityUrl.EnsureEndsWith("/");
    }
}
