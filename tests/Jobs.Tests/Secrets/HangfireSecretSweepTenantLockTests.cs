using Hangfire;
using Hangfire.Storage;
using Meshmakers.Octo.Backend.Jobs.Secrets;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Secrets;

/// <summary>
///     AB#5539 — per-tenant mutual exclusion of secret sweeps on a Hangfire distributed lock.
/// </summary>
public class HangfireSecretSweepTenantLockTests
{
    private readonly IStorageConnection _connection = Substitute.For<IStorageConnection>();
    private readonly HangfireSecretSweepTenantLock _lock;

    public HangfireSecretSweepTenantLockTests()
    {
        var storage = Substitute.For<JobStorage>();
        storage.GetConnection().Returns(_connection);
        _lock = new HangfireSecretSweepTenantLock(() => storage);
    }

    [Test]
    public async Task TryAcquire_LocksTheNormalizedTenant_AndReleasesLockAndConnectionOnDispose()
    {
        var handle = Substitute.For<IDisposable>();
        _connection.AcquireDistributedLock(Arg.Any<string>(), Arg.Any<TimeSpan>()).Returns(handle);

        var held = _lock.TryAcquire("Tenant-A", TimeSpan.FromSeconds(3));

        await Assert.That(held).IsNotNull();
        _connection.Received(1).AcquireDistributedLock(
            HangfireSecretSweepTenantLock.ResourcePrefix + "tenant-a", TimeSpan.FromSeconds(3));
        _connection.DidNotReceive().Dispose();

        held!.Dispose();

        handle.Received(1).Dispose();
        _connection.Received(1).Dispose();
    }

    [Test]
    public async Task TryAcquire_Timeout_ReturnsNull_AndDisposesTheConnection()
    {
        _connection.AcquireDistributedLock(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Throws(new DistributedLockTimeoutException("octo:secret-sweep:lock:t"));

        var held = _lock.TryAcquire("t", TimeSpan.FromSeconds(1));

        await Assert.That(held).IsNull();
        _connection.Received(1).Dispose();
    }
}
