using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.BotServices.Configuration;

internal class ConfigureOpenIdConnectOptions : IConfigureNamedOptions<OpenIdConnectOptions>
{
    private readonly IOptions<OctoBotServicesOptions> _octoBotServicesOptions;

    public ConfigureOpenIdConnectOptions(IOptions<OctoBotServicesOptions> octoBotServicesOptions)
    {
        _octoBotServicesOptions = octoBotServicesOptions;
    }

    public void Configure(OpenIdConnectOptions options)
    {
        Configure(Options.DefaultName, options);
    }

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        options.Authority = _octoBotServicesOptions.Value.AuthorityUrl;
    }
}
