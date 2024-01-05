using Hangfire;
using Hangfire.Storage;
using Meshmakers.Octo.Backend.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure.Initialization;

namespace Meshmakers.Octo.Backend.BotServices.Services;

/// <summary>
///     Implements the service hook service, that creates hangfire jobs for each data source
/// </summary>
internal class ServiceHookService : IAsyncInitializationService
{
    private readonly ISystemContext _systemContext;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="systemContext"></param>
    public ServiceHookService(ISystemContext systemContext/*, IDistributedWithPubSubCache distributedCache*/)
    {
        _systemContext = systemContext;
        // var sub = distributedCache.Subscribe<string>(CacheCommon.KeyTenantUpdate);
        // sub.OnMessage(async _ => { await SyncDataSourceAndCreateJobsAsync(); });
    }

    public int Order => 10;

    /// <summary>
    ///     Removes all jobs and creates new jobs for existing data sources
    /// </summary>
    /// <returns></returns>
    public async Task InitializeAsync()
    {
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