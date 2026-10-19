using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Meshmakers.Octo.Backend.BotServices.Consumers;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using NSubstitute;
using NSubstitute.Core;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Consumers;

/// <summary>
///     AB#6734 — the consumer refuses an export request without caller identity before a job is enqueued (fail
///     closed), and enqueues one that carries it with the request - subject and roles included - as the job
///     argument.
/// </summary>
public class ModelCommandsConsumerExportCallerTests
{
    private readonly IBackgroundJobClient _jobs = Substitute.For<IBackgroundJobClient>();

    public ModelCommandsConsumerExportCallerTests()
    {
        _jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("job-1");
    }

    private static IDistributedContext<T> Context<T>(T message) where T : class
    {
        var context = Substitute.For<IDistributedContext<T>>();
        context.Message.Returns(message);
        return context;
    }

    [Test]
    public async Task Query_WithoutCallerIdentity_IsRefused_NoJobEnqueued()
    {
        var consumer = new ModelCommandsConsumer(_jobs);
        var context = Context(new ExportRtByQueryCommandRequest("tenant-1", OctoObjectId.GenerateNewId()));

        var exception = await Assert.That(async () => await consumer.ConsumeAsync(context))
            .Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains("carries no caller identity");
        _jobs.DidNotReceiveWithAnyArgs().Create(default!, default!);
        await context.DidNotReceiveWithAnyArgs().RespondAsync<JobCreatedResponse>(default!);
    }

    [Test]
    public async Task DeepGraph_WithoutCallerIdentity_IsRefused_NoJobEnqueued()
    {
        var consumer = new ModelCommandsConsumer(_jobs);
        var context = Context(new ExportRtByDeepGraphCommandRequest("tenant-1", [OctoObjectId.GenerateNewId()],
            new RtCkId<CkTypeId>("Test/Customer")) { InitiatedBySubjectId = " " });

        await Assert.That(async () => await consumer.ConsumeAsync(context)).Throws<InvalidOperationException>();

        _jobs.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Test]
    public async Task Query_WithCallerIdentity_EnqueuesTheRequestWithTheCaller()
    {
        var consumer = new ModelCommandsConsumer(_jobs);
        var context = Context(new ExportRtByQueryCommandRequest("tenant-1", OctoObjectId.GenerateNewId())
        {
            InitiatedBySubjectId = "user-1",
            InitiatedByRoleNames = ["Reader"]
        });

        await consumer.ConsumeAsync(context);

        var job = (Job)_jobs.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IBackgroundJobClient.Create))
            .GetArguments()[0]!;
        var enqueued = (ExportRtByQueryCommandRequest)job.Args.Single(a => a is ExportRtByQueryCommandRequest)!;
        await Assert.That(enqueued.InitiatedBySubjectId).IsEqualTo("user-1");
        await Assert.That(enqueued.InitiatedByRoleNames!.Single()).IsEqualTo("Reader");
        await context.Received(1).RespondAsync(Arg.Is<JobCreatedResponse>(r => r.JobId == "job-1"));
    }
}
