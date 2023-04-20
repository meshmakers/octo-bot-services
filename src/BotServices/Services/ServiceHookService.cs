using System.Threading.Tasks;
using Hangfire;
using Hangfire.Storage;
using Meshmakers.Octo.Backend.BotServices.Jobs;
using Meshmakers.Octo.Backend.DistributedCache;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.SystematizedData.Persistence;

namespace Meshmakers.Octo.Backend.BotServices.Services;

/// <summary>
///     Implements the service hook service, that creates hangfire jobs for each data source
/// </summary>
public class ServiceHookService : IServiceHookService
{
    private readonly ISystemContext _systemContext;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="systemContext"></param>
    /// <param name="distributedCache"></param>
    public ServiceHookService(ISystemContext systemContext, IDistributedWithPubSubCache distributedCache)
    {
        _systemContext = systemContext;
        var sub = distributedCache.Subscribe<string>(CacheCommon.KeyTenantUpdate);
        sub.OnMessage(async message => { await SyncDataSourceAndCreateJobsAsync(); });
    }

    /// <summary>
    ///     Removes all jobs and creates new jobs for existing data sources
    /// </summary>
    /// <returns></returns>
    public async Task SyncDataSourceAndCreateJobsAsync()
    {
        using (var connection = JobStorage.Current.GetConnection())
        {
            foreach (var recurringJob in connection.GetRecurringJobs())
            {
                RecurringJob.RemoveIfExists(recurringJob.Id);
            }
        }

        using var systemSession = await _systemContext.StartSystemSessionAsync();
        systemSession.StartTransaction();

        // Clean old jobs
        var result = await _systemContext.GetTenantsAsync(systemSession);
        foreach (var octoTenant in result.List)
        {
            RecurringJob.AddOrUpdate<ServiceHookJob>($"ServiceHook_{octoTenant.TenantId}",
                job => job.Run(octoTenant.TenantId, JobCancellationToken.Null), "*/15 * * * *");

            RecurringJob.AddOrUpdate<AttributeValueAggregatorJob>($"AttributeValueAggregate_{octoTenant.TenantId}",
                job => job.Run(octoTenant.TenantId, JobCancellationToken.Null), Cron.Daily);
            RecurringJob.AddOrUpdate<EMailSenderJob>($"Notification_EMail_Sender_{octoTenant.TenantId}",
                job => job.SendEMail(octoTenant.TenantId, JobCancellationToken.Null), Cron.Minutely);
        }

        await systemSession.CommitTransactionAsync();
    }
}
