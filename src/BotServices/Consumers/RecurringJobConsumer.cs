using Hangfire;
using Hangfire.Storage;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

namespace Meshmakers.Octo.Backend.BotServices.Consumers;

// ReSharper disable once ClassNeverInstantiated.Global
internal class RecurringJobConsumer : IDistributedConsumer<RemoveRecurringJobsByScheduleGroupRequest>
{
    public async Task ConsumeAsync(IDistributedContext<RemoveRecurringJobsByScheduleGroupRequest> context)
    {
        using var connection = JobStorage.Current.GetConnection();
        
        // Clean old jobs
        foreach (var recurringJob in connection.GetRecurringJobs())
        {
            if (recurringJob.Id.EndsWith(context.Message.ScheduleGroup))
            {
                RecurringJob.RemoveIfExists(recurringJob.Id);
            }
        }
        await context.RespondAsync(new GenericCommandResponse());
    }
}