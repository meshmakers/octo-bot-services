using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.BotServices.Consumers;

/// <summary>
///    Updates jobs for a tenant. AB#8447: when a tenant is deleted, its recurring jobs and its method operation records
///    (system database, <see cref="ICkMethodOperationTenantPurge" />) are removed, so a new tenant with the same id never
///    reads an operation of the old one. A restore is not a delete and keeps the records.
/// </summary>
/// <remarks>
///     <c>PreDeleteTenant</c> is not only sent for a delete: a restore drops and re-attaches the tenant
///     (<c>TenantBackupService.RestoreTenantAsync</c>) and a detach sends it as well. The purge therefore runs only when the
///     tenant lifecycle record carries the <see cref="TenantLifecycleState.Deleting" /> tombstone, which the tenant delete
///     writes before it sends the event. Otherwise a restore started as a method would delete its own operation record
///     while it runs (found in the local end-to-end check of AB#8447).
/// </remarks>
// ReSharper disable once ClassNeverInstantiated.Global
internal class TenantManagementConsumer : IDistributedConsumer<PosUpdateTenant>,
    IDistributedConsumer<PosCreateTenant>,
    IDistributedConsumer<PreDeleteTenant>
{
    private readonly ILogger<TenantManagementConsumer> _logger;
    private readonly IOptions<OctoBotServicesOptions> _options;
    private readonly IJobCreatorService _jobCreatorService;
    private readonly ICkMethodOperationTenantPurge _operationPurge;
    private readonly ITenantLifecycleStore _tenantLifecycleStore;

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="options"></param>
    /// <param name="jobCreatorService"></param>
    /// <param name="operationPurge">Removes the method operation records of a deleted tenant (AB#8447).</param>
    /// <param name="tenantLifecycleStore">Tells a delete (Deleting tombstone) from a restore or detach (AB#8447).</param>
    public TenantManagementConsumer(ILogger<TenantManagementConsumer> logger,
        IOptions<OctoBotServicesOptions> options,
        IJobCreatorService jobCreatorService,
        ICkMethodOperationTenantPurge operationPurge,
        ITenantLifecycleStore tenantLifecycleStore)
    {
        _logger = logger;
        _options = options;
        _jobCreatorService = jobCreatorService;
        _operationPurge = operationPurge;
        _tenantLifecycleStore = tenantLifecycleStore;
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
            if (!await IsBeingDeletedAsync(tenantId))
            {
                _logger.LogInformation(
                    "Tenant '{TenantId}' is removed without a delete (restore or detach); its method operation records are kept",
                    tenantId);
                return;
            }

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

    private async Task<bool> IsBeingDeletedAsync(string tenantId)
    {
        var lifecycle = await _tenantLifecycleStore.GetAsync(tenantId.NormalizeString());
        return lifecycle?.State == TenantLifecycleState.Deleting;
    }
}