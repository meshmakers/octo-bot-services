using Meshmakers.Octo.Common.DistributionEventHub.Configuration;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

namespace Meshmakers.Octo.Backend.BotServices.Consumers;

/// <summary>
///     The command consumers of the bot service (AB#6910). Every replica of the service consumes them: they are
///     registered on the shared (competing-consumer) topology of the event hub, so each command is processed exactly
///     once by one of the running replicas.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="CommandConsumerOptions.MigratesLegacyCommand" /> is required: until AB#6910 these commands were
///         served by the classic exclusive consumer, and their senders (asset repository, communication controller, AI
///         and MCP service: <c>AddCommandClient</c>) still send to the classic address. With the option the consumer
///         uses the queue <c>{name}::shared</c> and serves the classic address as soon as no old (exclusive) replica
///         is alive, so a rolling update from an old to a new build neither fails to start nor loses or duplicates a
///         command (event hub <c>docs/DEVELOPER.md</c>, "Migrating exclusive command queues").
///     </para>
///     <para>
///         Production code and the two-replica tests (<c>Jobs.IntegrationTests/BotCommandConsumerReplicaTests</c>) use
///         this one registration, so the tests prove the real topology.
///     </para>
/// </remarks>
internal static class BotCommandConsumers
{
    /// <summary>The consumer options of every bot command: shared, migrating the classic exclusive queue.</summary>
    internal static readonly CommandConsumerOptions SharedOptions = new() { MigratesLegacyCommand = true };

    /// <summary>Registers the six command consumers of the bot service on the shared topology.</summary>
    internal static void AddBotCommandConsumers(this IDistributionEventHubConfiguration config)
    {
        // Safe under competing consumers: ModelCommandsConsumer only enqueues a Hangfire job (IBackgroundJobClient)
        // and answers with its id. It holds no state between messages; the job runs on whichever Hangfire server
        // picks it from the shared MongoDB job storage, as before. One replica already processed these commands
        // concurrently (no concurrency limit on the endpoint), so a second replica adds no new race.
        config.AddCommandConsumer<ModelCommandsConsumer, ImportCkCommandRequest>(QueueNames.ImportCkCommand,
            SharedOptions);
        // Safe: as ImportCk (Hangfire enqueue only).
        config.AddCommandConsumer<ModelCommandsConsumer, ImportCkBatchCommandRequest>(
            QueueNames.ImportCkBatchCommand, SharedOptions);
        // Safe: as ImportCk (Hangfire enqueue only).
        config.AddCommandConsumer<ModelCommandsConsumer, ImportRtCommandRequest>(QueueNames.ImportRtCommand,
            SharedOptions);
        // Safe: as ImportCk (caller check, then Hangfire enqueue only).
        config.AddCommandConsumer<ModelCommandsConsumer, ExportRtByQueryCommandRequest>(
            QueueNames.ExportRtByQueryCommand, SharedOptions);
        // Safe: as ImportCk (caller check, then Hangfire enqueue only).
        config.AddCommandConsumer<ModelCommandsConsumer, ExportRtByDeepGraphCommandRequest>(
            QueueNames.ExportRtByDeepGraphCommand, SharedOptions);
        // Safe under competing consumers: RecurringJobConsumer removes the recurring jobs of a schedule group from the
        // shared Hangfire storage with the idempotent RemoveIfExists; it holds no state between messages. Two
        // replicas removing the same group at once remove each job once, a removal of a missing job is a no-op.
        config.AddCommandConsumer<RecurringJobConsumer, RemoveRecurringJobsByScheduleGroupRequest>(
            QueueNames.RemoveRecurringJobsByScheduleGroupCommand, SharedOptions);
    }
}
