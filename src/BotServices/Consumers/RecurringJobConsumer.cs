using Hangfire;
using Hangfire.Storage;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

namespace Meshmakers.Octo.Backend.BotServices.Consumers;

/// <summary>
///     Removes the recurring jobs of a schedule group. Runs on every replica (shared command queue, AB#6910): it holds
///     no state between messages and removes with the idempotent <see cref="IRecurringJobManager.RemoveIfExists" />.
/// </summary>
/// <remarks>
///     Storage and manager come from DI (registered by <c>AddHangfire</c>: the same storage as
///     <see cref="JobStorage.Current" />) instead of the static facade, so the consumer can run in tests without
///     setting process-wide Hangfire state.
/// </remarks>
// ReSharper disable once ClassNeverInstantiated.Global
internal class RecurringJobConsumer(JobStorage jobStorage, IRecurringJobManager recurringJobManager)
    : IDistributedConsumer<RemoveRecurringJobsByScheduleGroupRequest>
{
    public async Task ConsumeAsync(IDistributedContext<RemoveRecurringJobsByScheduleGroupRequest> context)
    {
        using var connection = jobStorage.GetConnection();

        // Clean old jobs
        foreach (var recurringJob in connection.GetRecurringJobs())
        {
            if (recurringJob.Id.EndsWith(context.Message.ScheduleGroup))
            {
                recurringJobManager.RemoveIfExists(recurringJob.Id);
            }
        }

        await context.RespondAsync(new GenericCommandResponse());
    }
}
