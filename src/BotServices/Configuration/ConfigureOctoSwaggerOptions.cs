using Meshmakers.Common.Shared;
using Meshmakers.Octo.Services.Swagger;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.BotServices.Configuration;

// ReSharper disable once ClassNeverInstantiated.Global
internal class ConfigureOctoSwaggerOptions : IConfigureNamedOptions<OctoSwaggerOptions>
{
    private readonly IOptions<OctoBotServicesOptions> _octoBotOptions;

    public ConfigureOctoSwaggerOptions(IOptions<OctoBotServicesOptions> octoBotOptions)
    {
        _octoBotOptions = octoBotOptions;
    }

    public void Configure(OctoSwaggerOptions options)
    {
        Configure(Options.DefaultName, options);
    }

    public void Configure(string? name, OctoSwaggerOptions options)
    {
        options.AuthorityUrl = _octoBotOptions.Value.AuthorityUrl.EnsureEndsWith("/");
    }
}