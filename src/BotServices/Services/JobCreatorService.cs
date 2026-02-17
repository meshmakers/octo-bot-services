using Hangfire;
using Hangfire.Storage;
using Meshmakers.Octo.Backend.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs;

namespace Meshmakers.Octo.Backend.BotServices.Services;

internal class JobCreatorService : IJobCreatorService
{
    public void CreateJobs(string instancePrefix, string tenantId)
    {
        // Create new jobs
        RecurringJob.AddOrUpdate<IAttributeValueAggregatorJob>($"{instancePrefix}:{tenantId}_AttributeValueAggregate",
            job => job.Run(tenantId, BotCancellationToken.Null), Cron.Daily);
    }

    public void DeleteJobs(string instancePrefix, string tenantId)
    {
        using var connection = JobStorage.Current.GetConnection();
        // Clean old jobs
        foreach (var recurringJob in connection.GetRecurringJobs())
        {
            if (recurringJob.Id.StartsWith($"{instancePrefix}:{tenantId}_AttributeValueAggregate"))
            {
                RecurringJob.RemoveIfExists(recurringJob.Id);
            }
        }
    }
}