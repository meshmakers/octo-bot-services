using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Meshmakers.Octo.Backend.BotServices.Consumers;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using NSubstitute;
using NSubstitute.Core;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Consumers;

/// <summary>
///     AB#8705 — the consumer enqueues the RT import with the subject and the principal kind of the initiating caller,
///     so the job can hand both to the engine (types with a <c>writeAccess</c> block, AB#8574). A message of an older
///     producer carries no kind and is enqueued as <see cref="RtPrincipalKind.Unknown" />.
/// </summary>
public class ModelCommandsConsumerImportCallerTests
{
    private readonly IBackgroundJobClient _jobs = Substitute.For<IBackgroundJobClient>();

    public ModelCommandsConsumerImportCallerTests()
    {
        _jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("job-1");
    }

    private static IDistributedContext<T> Context<T>(T message) where T : class
    {
        var context = Substitute.For<IDistributedContext<T>>();
        context.Message.Returns(message);
        return context;
    }

    private Job EnqueuedJob()
    {
        return (Job)_jobs.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IBackgroundJobClient.Create))
            .GetArguments()[0]!;
    }

    [Test]
    [Arguments(RtPrincipalKind.Interactive)]
    [Arguments(RtPrincipalKind.Client)]
    public async Task ImportRt_EnqueuesTheJobWithSubjectAndPrincipalKind(RtPrincipalKind kind)
    {
        var consumer = new ModelCommandsConsumer(_jobs);
        var context = Context(new ImportRtCommandRequest("tenant-1", ImportStrategy.Upsert, "cache-key")
        {
            InitiatedBySubjectId = "user-1",
            InitiatedByPrincipalKind = kind
        });

        await consumer.ConsumeAsync(context);

        var job = EnqueuedJob();
        await Assert.That(job.Method.Name).IsEqualTo(nameof(IImportModelJob.ImportRtAsync));
        await Assert.That(job.Method.GetParameters().Any(p => p.ParameterType == typeof(RtPrincipalKind))).IsTrue();
        await Assert.That(job.Args.Contains("user-1")).IsTrue();
        await Assert.That(job.Args.OfType<RtPrincipalKind>().Single()).IsEqualTo(kind);
        await context.Received(1).RespondAsync(Arg.Is<JobCreatedResponse>(r => r.JobId == "job-1"));
    }

    [Test]
    public async Task ImportRt_MessageOfAnOlderProducer_IsEnqueuedAsUnknown()
    {
        var consumer = new ModelCommandsConsumer(_jobs);
        var context = Context(new ImportRtCommandRequest("tenant-1", ImportStrategy.Insert, "cache-key")
        {
            InitiatedBySubjectId = "user-1"
        });

        await consumer.ConsumeAsync(context);

        await Assert.That(EnqueuedJob().Args.OfType<RtPrincipalKind>().Single()).IsEqualTo(RtPrincipalKind.Unknown);
    }
}
