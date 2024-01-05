using Hangfire;
using Hangfire.Storage;
using Meshmakers.Octo.Backend.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.BotServices.Consumers;

/// <summary>
///     Handles the <see cref="PreUpdateTenant" /> message.
/// </summary>
internal class PreUpdateTenantConsumer : IDistributedConsumer<PreUpdateTenant>
{
    private readonly ILogger<PreUpdateTenantConsumer> _logger;
    private readonly ISystemContext _systemContext;

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="systemContext"></param>
    public PreUpdateTenantConsumer(ILogger<PreUpdateTenantConsumer> logger, ISystemContext systemContext)
    {
        _logger = logger;
        _systemContext = systemContext;
    }

    /// <summary>
    ///     Removes all jobs and creates new jobs for existing data sources
    /// </summary>
    /// <returns></returns>
    public async Task ConsumeAsync(IDistributedContext<PreUpdateTenant> context)
    {
        _logger.LogInformation("Pre update tenant received: {Text}", context.Message.TenantId);

        using (var connection = JobStorage.Current.GetConnection())
        {
            foreach (var recurringJob in connection.GetRecurringJobs())
            {
                RecurringJob.RemoveIfExists(recurringJob.Id);
            }
        }

        using var systemSession = await _systemContext.GetSystemSessionAsync();
        systemSession.StartTransaction();

        // Clean old jobs
        var result = await _systemContext.GetChildTenantsAsync(systemSession);
        foreach (var octoTenant in result.Items)
        {
            RecurringJob.AddOrUpdate<IServiceHookJob>($"ServiceHook_{octoTenant.TenantId}",
                job => job.Run(octoTenant.TenantId, BotCancellationToken.Null), "*/15 * * * *");

            RecurringJob.AddOrUpdate<IAttributeValueAggregatorJob>($"AttributeValueAggregate_{octoTenant.TenantId}",
                job => job.Run(octoTenant.TenantId, BotCancellationToken.Null), Cron.Daily);
            RecurringJob.AddOrUpdate<IEMailSenderJob>($"Notification_EMail_Sender_{octoTenant.TenantId}",
                job => job.SendEMail(octoTenant.TenantId, BotCancellationToken.Null), Cron.Minutely);
        }

        await systemSession.CommitTransactionAsync();
    }
}