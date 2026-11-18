using Meshmakers.Octo.Backend.BotServices.Services.DumpRegistry;

namespace Meshmakers.Octo.Backend.Jobs.Tests.DumpRegistry;

public class RetryingInitializationTests
{
    [Test]
    public async Task ASuccessfulInitialization_RunsOnce()
    {
        var runCount = 0;
        var initialization = new RetryingInitialization(() =>
        {
            runCount++;
            return Task.CompletedTask;
        });

        await initialization.EnsureAsync();
        await initialization.EnsureAsync();

        await Assert.That(runCount).IsEqualTo(1);
    }

    [Test]
    public async Task AFailedInitialization_RunsAgainOnTheNextCall()
    {
        var runCount = 0;
        var initialization = new RetryingInitialization(() =>
        {
            runCount++;
            return runCount == 1
                ? Task.FromException(new InvalidOperationException("database not reachable"))
                : Task.CompletedTask;
        });

        await Assert.That(async () => await initialization.EnsureAsync()).Throws<InvalidOperationException>();
        await initialization.EnsureAsync();
        await initialization.EnsureAsync();

        await Assert.That(runCount).IsEqualTo(2);
    }

    [Test]
    public async Task ConcurrentCallers_ShareTheRunningInitialization()
    {
        var runCount = 0;
        var gate = new TaskCompletionSource();
        var initialization = new RetryingInitialization(() =>
        {
            runCount++;
            return gate.Task;
        });

        var firstCaller = initialization.EnsureAsync();
        var secondCaller = initialization.EnsureAsync();
        gate.SetResult();
        await Task.WhenAll(firstCaller, secondCaller);

        await Assert.That(runCount).IsEqualTo(1);
    }
}
