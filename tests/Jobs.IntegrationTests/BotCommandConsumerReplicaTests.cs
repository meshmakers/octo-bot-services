using System.Collections.Concurrent;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Meshmakers.Octo.Backend.BotServices.Consumers;
using Meshmakers.Octo.Common.DistributionEventHub;
using Meshmakers.Octo.Common.DistributionEventHub.Configuration;
using Meshmakers.Octo.Common.DistributionEventHub.Configuration.Options;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Testcontainers.RabbitMq;

namespace Meshmakers.Octo.Backend.Jobs.IntegrationTests;

/// <summary>
///     AB#6910 — two replicas of the bot service consume its six command queues as competing consumers: every command
///     is processed exactly once, both replicas take a share, the caller gets its answer, a rolling update from the old
///     exclusive build loses nothing, and the service no longer logs the AB#6909 deprecation warning at start.
/// </summary>
/// <remarks>
///     The replicas use the production registration (<see cref="BotCommandConsumers.AddBotCommandConsumers" />) and
///     the real consumers on a private RabbitMQ (Testcontainers, needs Docker); only Hangfire is replaced by recording
///     fakes per replica, so the ledger shows which replica processed which command. The senders use the classic
///     <c>AddCommandClient</c>, exactly as the asset repository, communication controller, AI and MCP service do.
/// </remarks>
[NotInParallel(nameof(BotCommandConsumerReplicaTests))]
public class BotCommandConsumerReplicaTests
{
    private const int CommandsPerKind = 12;
    private const string DeprecationMarker = "classic EXCLUSIVE";

    private readonly string _instancePrefix = $"bot6910-{Guid.NewGuid():N}";
    private readonly List<Replica> _replicas = [];
    private readonly ConcurrentQueue<(string Key, string Replica)> _ledger = new();
    private readonly CapturingLoggerProvider _logs = new();

    [After(Test)]
    public async Task StopReplicasAsync()
    {

        foreach (var replica in _replicas.ToList())
        {
            await StopReplicaAsync(replica);
        }
    }

    [Test]
    public async Task TwoReplicas_EveryCommandIsProcessedOnce_BothReplicasConsume_CallerGetsTheAnswer()
    {
        await StartNewReplicaAsync("Bot-1");
        await StartNewReplicaAsync("Bot-2");
        var sender = await StartSenderAsync();
        await WaitUntilServedAsync(sender);
        _ledger.Clear();

        var keys = await SendAllKindsAsync(sender, "a");

        await Assert.That(_ledger.Count).IsEqualTo(keys.Count);
        foreach (var group in _ledger.GroupBy(e => e.Key))
        {
            await Assert.That(group.Count()).IsEqualTo(1).Because($"command '{group.Key}' must be processed once");
        }

        await Assert.That(_ledger.Select(e => e.Key).OrderBy(k => k)).IsEquivalentTo(keys.OrderBy(k => k));
        await Assert.That(_ledger.Select(e => e.Replica).Distinct().OrderBy(r => r))
            .IsEquivalentTo(new[] { "Bot-1", "Bot-2" }).Because("both replicas consume the shared queues");
        foreach (var kind in _ledger.GroupBy(e => e.Key.Split(':')[0]))
        {
            await Assert.That(kind.Count()).IsEqualTo(CommandsPerKind);
        }
    }

    [Test]
    public async Task Start_LogsNoDeprecationWarning_AndUsesTheSharedQueues()
    {
        await StartNewReplicaAsync("Bot-1");
        var sender = await StartSenderAsync();
        await WaitUntilServedAsync(sender);

        await Assert.That(_logs.Lines.Where(l => l.Contains(DeprecationMarker))).IsEmpty();
        await Assert.That(BotCommandConsumers.SharedOptions.MigratesLegacyCommand).IsTrue();
    }

    [Test]
    public async Task RollingUpdate_OldExclusiveAndNewSharedReplica_StartWithoutError_NoCommandLostOrDuplicated()
    {
        // The old build: the same consumers with the classic exclusive registration (before AB#6910).
        var old = await StartReplicaAsync("Old-1", RegisterClassic);
        var sender = await StartSenderAsync();
        await Assert.That(_logs.Lines.Any(l => l.Contains(DeprecationMarker))).IsTrue()
            .Because("positive control: the old build logs the deprecation warning, so its absence elsewhere counts");

        // The new build joins while the old one owns the classic queues: no PRECONDITION_FAILED / RESOURCE_LOCKED.
        await StartNewReplicaAsync("Bot-1");
        await Task.Delay(TimeSpan.FromSeconds(5));

        _ledger.Clear();
        var phaseA = await SendAllKindsAsync(sender, "old");
        await Assert.That(_ledger.Count).IsEqualTo(phaseA.Count);
        await Assert.That(_ledger.All(e => e.Replica == "Old-1")).IsTrue()
            .Because("while an old replica runs, it keeps the classic traffic and the new one stands by");

        // The old replica goes away: the new one takes over the classic address.
        await StopReplicaAsync(old);
        await StartNewReplicaAsync("Bot-2");
        await WaitUntilServedAsync(sender);
        _ledger.Clear();

        var phaseB = await SendAllKindsAsync(sender, "new");
        await Assert.That(_ledger.Count).IsEqualTo(phaseB.Count);
        foreach (var group in _ledger.GroupBy(e => e.Key))
        {
            await Assert.That(group.Count()).IsEqualTo(1);
        }

        await Assert.That(_ledger.All(e => e.Replica is "Bot-1" or "Bot-2")).IsTrue();
    }

    // -------------------------------------------------------------------------------------------------------------
    // commands

    /// <summary>Sends <see cref="CommandsPerKind" /> commands of each of the six kinds at once; returns their keys.</summary>
    private static async Task<IReadOnlyList<string>> SendAllKindsAsync(Replica sender, string run)
    {
        var p = sender.Provider;
        var tasks = new List<Task<string>>();
        for (var i = 0; i < CommandsPerKind; i++)
        {
            var n = $"{run}-{i:D3}";
            tasks.Add(SendJobAsync(p, new ImportCkCommandRequest($"ImportCk:{n}", "cache-key")));
            tasks.Add(SendJobAsync(p, new ImportCkBatchCommandRequest($"ImportCkBatch:{n}", ["cache-key"])));
            tasks.Add(SendJobAsync(p, new ImportRtCommandRequest($"ImportRt:{n}", ImportStrategy.Upsert, "cache-key")));
            tasks.Add(SendJobAsync(p, new ExportRtByQueryCommandRequest($"ExportRtByQuery:{n}",
                OctoObjectId.GenerateNewId()) { InitiatedBySubjectId = "user-1" }));
            tasks.Add(SendJobAsync(p, new ExportRtByDeepGraphCommandRequest($"ExportRtByDeepGraph:{n}",
                [OctoObjectId.GenerateNewId()], new RtCkId<CkTypeId>("Test/Customer"))
            {
                InitiatedBySubjectId = "user-1"
            }));
            tasks.Add(SendRemoveRecurringAsync(p, $"RemoveRecurring:{n}"));
        }

        return await Task.WhenAll(tasks);
    }

    private static async Task<string> SendJobAsync<TRequest>(IServiceProvider provider, TRequest request)
        where TRequest : class
    {
        using var scope = provider.CreateScope();
        var response = await scope.ServiceProvider.GetRequiredService<ICommandClient<TRequest>>()
            .GetResponse<JobCreatedResponse>(request, CancellationToken.None, TimeSpan.FromSeconds(30));
        if (string.IsNullOrEmpty(response.JobId))
        {
            throw new InvalidOperationException("The caller got no job id.");
        }

        return TenantOf(request);
    }

    private static async Task<string> SendRemoveRecurringAsync(IServiceProvider provider, string group)
    {
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ICommandClient<RemoveRecurringJobsByScheduleGroupRequest>>()
            .GetResponse<GenericCommandResponse>(new RemoveRecurringJobsByScheduleGroupRequest(group),
                CancellationToken.None, TimeSpan.FromSeconds(30));
        return group;
    }

    private static string TenantOf(object request) => request switch
    {
        ImportCkCommandRequest r => r.TenantId,
        ImportCkBatchCommandRequest r => r.TenantId,
        ImportRtCommandRequest r => r.TenantId,
        ExportRtByQueryCommandRequest r => r.TenantId,
        ExportRtByDeepGraphCommandRequest r => r.TenantId,
        _ => throw new ArgumentOutOfRangeException(nameof(request))
    };

    /// <summary>
    ///     Probes until a replica answers on the classic address (the bridge serves it once no exclusive consumer is
    ///     alive, after at most one probe interval).
    /// </summary>
    private static async Task WaitUntilServedAsync(Replica sender)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var scope = sender.Provider.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ICommandClient<RemoveRecurringJobsByScheduleGroupRequest>>()
                    .GetResponse<GenericCommandResponse>(new RemoveRecurringJobsByScheduleGroupRequest("probe"),
                        CancellationToken.None, TimeSpan.FromSeconds(1));
                return;
            }
            catch (DistributionException)
            {
                // hand-over window: nothing serves the classic address yet
            }

            await Task.Delay(200);
        }

        throw new TimeoutException("No replica served the classic command address within 60 s.");
    }

    // -------------------------------------------------------------------------------------------------------------
    // replicas

    private sealed record Replica(string Name, ServiceProvider Provider);

    private Task<Replica> StartNewReplicaAsync(string name) =>
        StartReplicaAsync(name, config => config.AddBotCommandConsumers());

    private static void RegisterClassic(IDistributionEventHubConfiguration c)
    {
        c.AddCommandConsumer<ModelCommandsConsumer, ImportCkCommandRequest>(QueueNames.ImportCkCommand);
        c.AddCommandConsumer<ModelCommandsConsumer, ImportCkBatchCommandRequest>(QueueNames.ImportCkBatchCommand);
        c.AddCommandConsumer<ModelCommandsConsumer, ImportRtCommandRequest>(QueueNames.ImportRtCommand);
        c.AddCommandConsumer<ModelCommandsConsumer, ExportRtByQueryCommandRequest>(QueueNames.ExportRtByQueryCommand);
        c.AddCommandConsumer<ModelCommandsConsumer, ExportRtByDeepGraphCommandRequest>(
            QueueNames.ExportRtByDeepGraphCommand);
        c.AddCommandConsumer<RecurringJobConsumer, RemoveRecurringJobsByScheduleGroupRequest>(
            QueueNames.RemoveRecurringJobsByScheduleGroupCommand);
    }

    private Task<Replica> StartSenderAsync() => StartReplicaAsync("Sender", c =>
    {
        c.AddCommandClient<ImportCkCommandRequest>(QueueNames.ImportCkCommand);
        c.AddCommandClient<ImportCkBatchCommandRequest>(QueueNames.ImportCkBatchCommand);
        c.AddCommandClient<ImportRtCommandRequest>(QueueNames.ImportRtCommand);
        c.AddCommandClient<ExportRtByQueryCommandRequest>(QueueNames.ExportRtByQueryCommand);
        c.AddCommandClient<ExportRtByDeepGraphCommandRequest>(QueueNames.ExportRtByDeepGraphCommand);
        c.AddCommandClient<RemoveRecurringJobsByScheduleGroupRequest>(QueueNames.RemoveRecurringJobsByScheduleGroupCommand);
    });

    private async Task<Replica> StartReplicaAsync(string name, Action<IDistributionEventHubConfiguration> register)
    {
        var broker = await RabbitMqBroker.GetAsync();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(_logs).SetMinimumLevel(LogLevel.Information));
        AddRecordingHangfire(services, name);
        services.Configure<DistributionEventHubOptions>(o =>
        {
            o.InstancePrefix = _instancePrefix;
            o.BrokerHost = broker.Host;
            o.BrokerPort = (ushort)broker.Port;
            o.BrokerUser = RabbitMqBroker.User;
            o.BrokerPassword = RabbitMqBroker.Password;
        });
        services.AddDistributionEventHub(config =>
        {
            config.UniqueServiceAddress = name;
            config.AutomaticallyStartBusDuringStartup = false;
            register(config);
        });

        var provider = services.BuildServiceProvider();
        var replica = new Replica(name, provider);
        _replicas.Add(replica);
        await provider.GetRequiredService<IEventHubControl>().StartAsync(CancellationToken.None);
        return replica;
    }

    private async Task StopReplicaAsync(Replica replica)
    {
        if (!_replicas.Remove(replica))
        {
            return;
        }

        try
        {
            await replica.Provider.GetRequiredService<IEventHubControl>().StopAsync();
        }
        catch
        {
            // best effort
        }

        await replica.Provider.DisposeAsync();
    }

    /// <summary>
    ///     Hangfire of one replica: the job client and the recurring job manager record into the ledger which replica
    ///     processed which command (key = tenant id of the import/export request, schedule group of the removal).
    /// </summary>
    private void AddRecordingHangfire(IServiceCollection services, string replica)
    {
        var jobClient = Substitute.For<IBackgroundJobClient>();
        jobClient.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns(call =>
        {
            var job = call.Arg<Job>();
            // Import jobs take the tenant id as first argument, export jobs the request.
            var tenant = job.Args[0] as string ?? TenantOf(job.Args[0]!);
            _ledger.Enqueue((tenant, replica));
            return $"{replica}-{Guid.NewGuid():N}";
        });

        // The storage lists one recurring job per group the test sends (id = group); the removal of the matching job
        // is the evidence that this replica processed the command.
        var connection = Substitute.For<IStorageConnection>();
        connection.GetAllItemsFromSet("recurring-jobs").Returns(_ => new HashSet<string>(KnownGroups()));
        connection.GetAllEntriesFromHash(Arg.Any<string>())
            .Returns(_ => new Dictionary<string, string> { ["Cron"] = "0 0 * * *" });
        var storage = Substitute.For<JobStorage>();
        storage.GetConnection().Returns(connection);

        var recurring = Substitute.For<IRecurringJobManager>();
        recurring.When(m => m.RemoveIfExists(Arg.Any<string>()))
            .Do(call =>
            {
                var id = call.Arg<string>();
                if (id != "probe")
                {
                    _ledger.Enqueue((id, replica));
                }
            });

        services.AddSingleton(jobClient);
        services.AddSingleton(storage);
        services.AddSingleton(recurring);
    }

    private static IEnumerable<string> KnownGroups()
    {
        foreach (var run in new[] { "a", "old", "new" })
        {
            for (var i = 0; i < CommandsPerKind; i++)
            {
                yield return $"RemoveRecurring:{run}-{i:D3}";
            }
        }
    }

    // -------------------------------------------------------------------------------------------------------------
    // infrastructure

    /// <summary>One RabbitMQ container for the test session (started on first use, removed by Testcontainers' reaper).</summary>
    private sealed class RabbitMqBroker
    {
        public const string User = "guest";
        public const string Password = "guest";
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static RabbitMqBroker? _shared;
        private readonly RabbitMqContainer _container;

        private RabbitMqBroker(RabbitMqContainer container) => _container = container;

        public string Host => _container.Hostname;
        public int Port => _container.GetMappedPublicPort(5672);

        public static async Task<RabbitMqBroker> GetAsync()
        {
            if (_shared != null)
            {
                return _shared;
            }

            await Gate.WaitAsync();
            try
            {
                if (_shared == null)
                {
                    var container = new RabbitMqBuilder("rabbitmq:3-management")
                        .WithUsername(User)
                        .WithPassword(Password)
                        .Build();
                    await container.StartAsync();
                    _shared = new RabbitMqBroker(container);
                }

                return _shared;
            }
            finally
            {
                Gate.Release();
            }
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => _lines.ToArray();

        public ILogger CreateLogger(string categoryName) => new Logger(_lines);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lines.Enqueue($"{logLevel}: {formatter(state, exception)}");
            }
        }
    }
}
