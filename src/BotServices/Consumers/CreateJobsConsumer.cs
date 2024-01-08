using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.BotServices.Consumers;

/// <summary>
///    Updates jobs for a tenant
/// </summary>
internal class CreateJobsConsumer : IDistributedConsumer<PosUpdateTenant>,
    IDistributedConsumer<PosCreateTenant>, 
    IDistributedConsumer<PreDeleteTenant>
{
    private readonly ILogger<CreateJobsConsumer> _logger;
    private readonly IJobCreatorService _jobCreatorService;

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="jobCreatorService"></param>
    public CreateJobsConsumer(ILogger<CreateJobsConsumer> logger, IJobCreatorService jobCreatorService)
    {
        _logger = logger;
        _jobCreatorService = jobCreatorService;
    }

    public Task ConsumeAsync(IDistributedContext<PosUpdateTenant> context)
    {
        _logger.LogInformation("Pre update tenant received: {Text}", context.Message.TenantId);

        _jobCreatorService.DeleteJobs(context.Message.TenantId);
        _jobCreatorService.CreateJobs(context.Message.TenantId);

        return Task.CompletedTask;
    }
    
    public Task ConsumeAsync(IDistributedContext<PosCreateTenant> context)
    {
        _jobCreatorService.CreateJobs(context.Message.TenantId);
        return Task.CompletedTask;
    }
    
    public Task ConsumeAsync(IDistributedContext<PreDeleteTenant> context)
    {
        _jobCreatorService.DeleteJobs(context.Message.TenantId);
        return Task.CompletedTask;
    }
}