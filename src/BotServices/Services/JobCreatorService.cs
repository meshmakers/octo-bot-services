using Hangfire;
using Hangfire.Storage;
using Meshmakers.Octo.Backend.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs;

namespace Meshmakers.Octo.Backend.BotServices.Services;

internal class JobCreatorService : IJobCreatorService
{
    public void CreateJobs(string tenantId)
    {
        // Create new jobs
        RecurringJob.AddOrUpdate<IServiceHookJob>($"{tenantId}_ServiceHook",
            job => job.Run(tenantId, BotCancellationToken.Null), "*/15 * * * *");
        RecurringJob.AddOrUpdate<IAttributeValueAggregatorJob>($"{tenantId}_AttributeValueAggregate",
            job => job.Run(tenantId, BotCancellationToken.Null), Cron.Daily);
        RecurringJob.AddOrUpdate<IEMailSenderJob>($"{tenantId}_Notification_EMail_Sender",
            job => job.SendEMail(tenantId, BotCancellationToken.Null), Cron.Minutely);
    }

    public void DeleteJobs(string tenantId)
    {
        using (var connection = JobStorage.Current.GetConnection())
        {
            // Clean old jobs
            foreach (var recurringJob in connection.GetRecurringJobs())
            {
                if (recurringJob.Id.StartsWith($"{tenantId}_ServiceHook") ||
                    recurringJob.Id.StartsWith($"{tenantId}_AttributeValueAggregate") ||
                    recurringJob.Id.StartsWith($"{tenantId}_Notification_EMail_Sender"))
                {
                    RecurringJob.RemoveIfExists(recurringJob.Id);
                }
            }
        }
    }
}