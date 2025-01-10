using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.BotServices.Configuration;

internal class ConfigureOpenIdConnectOptions(IOptions<OctoBotServicesOptions> octoBotServicesOptions)
    : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public void Configure(OpenIdConnectOptions options)
    {
        Configure(Options.DefaultName, options);
    }

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        options.Authority = octoBotServicesOptions.Value.AuthorityUrl;
    }
}