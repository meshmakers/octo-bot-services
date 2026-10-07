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
    public async Task Release_HappensOnTheAcquiringThread_EvenWhenDisposedFromAnotherThread()
    {
        int? acquiredOn = null;
        int? releasedOn = null;
        var handle = Substitute.For<IDisposable>();
        handle.When(h => h.Dispose()).Do(_ => releasedOn = Environment.CurrentManagedThreadId);
        _connection.AcquireDistributedLock(Arg.Any<string>(), Arg.Any<TimeSpan>()).Returns(_ =>
        {
            acquiredOn = Environment.CurrentManagedThreadId;
            return handle;
        });

        var held = _lock.TryAcquire("t", TimeSpan.FromSeconds(1));
        // A sweep awaits between acquiring and releasing: the release runs on another thread.
        await Task.Run(() => held!.Dispose());

        await Assert.That(acquiredOn).IsNotNull();
        await Assert.That(releasedOn).IsEqualTo(acquiredOn);
        handle.Received(1).Dispose();
        _connection.Received(1).Dispose();
        held!.Dispose(); // idempotent
        handle.Received(1).Dispose();
    }

    [Test]
    public async Task ThreadAffineStorageLock_IsFreeAgainRightAfterTheRun()
    {
        // AB#5539 live defect: Hangfire.Mongo releases a lock only when it is disposed on the thread that acquired
        // it. Released from another thread, the lock stayed until it expired and the next sweep was skipped
        // ("Another secret sweep of this tenant is running").
        var storageLock = new ThreadAffineLock();
        var storage = Substitute.For<JobStorage>();
        storage.GetConnection().Returns(_ =>
        {
            var connection = Substitute.For<IStorageConnection>();
            connection.AcquireDistributedLock(Arg.Any<string>(), Arg.Any<TimeSpan>())
                .Returns(_ => storageLock.Acquire());
            return connection;
        });
        var tenantLock = new HangfireSecretSweepTenantLock(() => storage);

        var first = tenantLock.TryAcquire("t", TimeSpan.FromMilliseconds(50));
        await Assert.That(first).IsNotNull();
        await Assert.That(tenantLock.TryAcquire("t", TimeSpan.FromMilliseconds(50))).IsNull(); // still excluded
        await Task.Run(() => first!.Dispose());

        var second = tenantLock.TryAcquire("t", TimeSpan.FromMilliseconds(50));
        await Assert.That(second).IsNotNull();
        second!.Dispose();
        await Assert.That(storageLock.IsHeld).IsFalse();
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

    /// <summary>
    ///     Mimics the Hangfire.Mongo distributed lock: one holder at a time; a release counts only on the thread that
    ///     acquired the lock.
    /// </summary>
    private sealed class ThreadAffineLock
    {
        private readonly object _gate = new();
        private int? _ownerThread;

        public bool IsHeld
        {
            get
            {
                lock (_gate)
                {
                    return _ownerThread != null;
                }
            }
        }

        public IDisposable Acquire()
        {
            lock (_gate)
            {
                if (_ownerThread != null)
                {
                    throw new DistributedLockTimeoutException("octo:secret-sweep:lock:t");
                }

                _ownerThread = Environment.CurrentManagedThreadId;
            }

            return new Release(this);
        }

        private sealed class Release(ThreadAffineLock owner) : IDisposable
        {
            public void Dispose()
            {
                lock (owner._gate)
                {
                    if (owner._ownerThread == Environment.CurrentManagedThreadId)
                    {
                        owner._ownerThread = null;
                    }
                }
            }
        }
    }
}
