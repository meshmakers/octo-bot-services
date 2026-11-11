using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.BotServices.Consumers;

/// <summary>
///    Updates jobs for a tenant. AB#8447: when a tenant is deleted, its recurring jobs and its method operation records
///    (system database, <see cref="ICkMethodOperationTenantPurge" />) are removed, so a new tenant with the same id never
///    reads an operation of the old one. A restore is not a delete and keeps the records.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal class TenantManagementConsumer : IDistributedConsumer<PosUpdateTenant>,
    IDistributedConsumer<PosCreateTenant>,
    IDistributedConsumer<PreDeleteTenant>
{
    private readonly ILogger<TenantManagementConsumer> _logger;
    private readonly IOptions<OctoBotServicesOptions> _options;
    private readonly IJobCreatorService _jobCreatorService;
    private readonly ICkMethodOperationTenantPurge _operationPurge;

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="options"></param>
    /// <param name="jobCreatorService"></param>
    /// <param name="operationPurge">Removes the method operation records of a deleted tenant (AB#8447).</param>
    public TenantManagementConsumer(ILogger<TenantManagementConsumer> logger,
        IOptions<OctoBotServicesOptions> options,
        IJobCreatorService jobCreatorService,
        ICkMethodOperationTenantPurge operationPurge)
    {
        _logger = logger;
        _options = options;
        _jobCreatorService = jobCreatorService;
        _operationPurge = operationPurge;
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

    public async Task ConsumeAsync(IDistributedContext<PreDeleteTenant> context)
    {
        var tenantId = context.Message.TenantId;
        _jobCreatorService.DeleteJobs(_options.Value.InstancePrefix ?? BotServiceConstants.DefaultInstancePrefix,
            tenantId);

        // Every replica receives the broadcast; the purge is idempotent. A failure must not block the tenant delete:
        // the records then expire with their retention (open ones are ended by the sweep first).
        try
        {
            var removed = await _operationPurge.DeleteTenantAsync(tenantId);
            _logger.LogInformation("Removed {Count} method operation record(s) of deleted tenant '{TenantId}'", removed,
                tenantId);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e,
                "Could not remove the method operation records of deleted tenant '{TenantId}'; they expire with their retention",
                tenantId);
        }
    }
}