using Meshmakers.Octo.Common.DistributionEventHub.Configuration.Options;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.BotServices.Configuration;

// ReSharper disable once ClassNeverInstantiated.Global
internal class ConfigureDistributionEventHubOptions : IConfigureNamedOptions<DistributionEventHubOptions>
{
    private readonly IOptions<OctoBotServicesOptions> _botServicesOptions;
    private readonly IOptions<OctoSystemConfiguration> _octoSystemConfiguration;

    public ConfigureDistributionEventHubOptions(IOptions<OctoBotServicesOptions> botServicesOptions,
        IOptions<OctoSystemConfiguration> octoSystemConfiguration)
    {
        _botServicesOptions = botServicesOptions;
        _octoSystemConfiguration = octoSystemConfiguration;
    }


    public void Configure(DistributionEventHubOptions options)
    {
        Configure(Options.DefaultName, options);
    }

    public void Configure(string? name, DistributionEventHubOptions options)
    {
        options.InstancePrefix = _botServicesOptions.Value.InstancePrefix;
        options.BrokerHost = _botServicesOptions.Value.BrokerHost;
        options.BrokerUser = _botServicesOptions.Value.BrokerUser;
        options.BrokerPassword = _botServicesOptions.Value.BrokerPassword;
        options.RepositoryHost = _octoSystemConfiguration.Value.DatabaseHost;
        options.RepositoryUser = _octoSystemConfiguration.Value.DatabaseUser;
        options.RepositoryPassword = _octoSystemConfiguration.Value.DatabaseUserPassword;
        options.DatabaseAuthenticationSource = _octoSystemConfiguration.Value.AuthenticationDatabaseName;
    }
}