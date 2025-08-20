using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.BotServices.Consumers;

/// <summary>
///    Updates jobs for a tenant
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal class TenantManagementConsumer : IDistributedConsumer<PosUpdateTenant>,
    IDistributedConsumer<PosCreateTenant>,
    IDistributedConsumer<PreDeleteTenant>
{
    private readonly ILogger<TenantManagementConsumer> _logger;
    private readonly IOptions<OctoBotServicesOptions> _options;
    private readonly IJobCreatorService _jobCreatorService;

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="options"></param>
    /// <param name="jobCreatorService"></param>
    public TenantManagementConsumer(ILogger<TenantManagementConsumer> logger,
        IOptions<OctoBotServicesOptions> options,
        IJobCreatorService jobCreatorService)
    {
        _logger = logger;
        _options = options;
        _jobCreatorService = jobCreatorService;
    }

    public Task ConsumeAsync(IDistributedContext<PosUpdateTenant> context)
    {
        _logger.LogInformation("Pre update tenant received: {Text}", context.Message.TenantId);

        _jobCreatorService.DeleteJobs(_options.Value.InstancePrefix ?? BotServiceConstants.DefaultInstancePrefix,
            context.Message.TenantId);
        _jobCreatorService.CreateJobs(_options.Value.InstancePrefix ?? BotServiceConstants.DefaultInstancePrefix,
            context.Message.TenantId);

        return Task.CompletedTask;
    }

    public Task ConsumeAsync(IDistributedContext<PosCreateTenant> context)
    {
        _jobCreatorService.CreateJobs(_options.Value.InstancePrefix ?? BotServiceConstants.DefaultInstancePrefix,
            context.Message.TenantId);
        return Task.CompletedTask;
    }

    public Task ConsumeAsync(IDistributedContext<PreDeleteTenant> context)
    {
        _jobCreatorService.DeleteJobs(_options.Value.InstancePrefix ?? BotServiceConstants.DefaultInstancePrefix,
            context.Message.TenantId);
        return Task.CompletedTask;
    }
}