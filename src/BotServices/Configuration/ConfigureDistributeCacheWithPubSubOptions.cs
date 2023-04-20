using Meshmakers.Octo.Backend.DistributedCache;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.BotServices.Configuration;

internal class ConfigureDistributeCacheWithPubSubOptions : IConfigureNamedOptions<DistributeCacheWithPubSubOptions>
{
    private readonly IOptions<OctoBotServicesOptions> _options;

    public ConfigureDistributeCacheWithPubSubOptions(IOptions<OctoBotServicesOptions> options)
    {
        _options = options;
    }

    public void Configure(DistributeCacheWithPubSubOptions options)
    {
        Configure(Options.DefaultName, options);
    }

    public void Configure(string? name, DistributeCacheWithPubSubOptions options)
    {
        options.Host = _options.Value.RedisCacheHost;
        options.Password = _options.Value.RedisCachePassword;
    }
}
