using Hangfire;
using Hangfire.Storage;

namespace Meshmakers.Octo.Backend.Jobs.Secrets;

/// <summary>
///     <see cref="ISecretSweepTenantLock" /> on a Hangfire distributed lock of the job storage, so it holds
///     across bot replicas.
/// </summary>
/// <remarks>
///     AB#5539: the Hangfire.Mongo lock is thread-affine - it counts acquisitions in a thread-local dictionary,
///     and a <c>Dispose</c> on another thread neither deletes the lock document nor clears that count. A sweep
///     acquires the lock, then awaits (dump, engine sweep) and is released on whatever thread resumes it: the lock
///     stayed in the database until it expired (~30 s after the run, so the next sweep was "Skipped - another
///     secret sweep of this tenant is running"), and the stale thread-local count let a later acquisition on the
///     original thread "succeed" without any lock. Acquisition and release therefore both happen on one
///     dedicated thread per held lock.
/// </remarks>
public class HangfireSecretSweepTenantLock : ISecretSweepTenantLock
{
    internal const string ResourcePrefix = "octo:secret-sweep:lock:";

    /// <summary>
    ///     How long <see cref="IDisposable.Dispose" /> of a held lock waits for the release to finish.
    /// </summary>
    internal static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<JobStorage> _storage;

    /// <summary>
    ///     Constructor using <see cref="JobStorage.Current" />.
    /// </summary>
    public HangfireSecretSweepTenantLock() : this(() => JobStorage.Current)
    {
    }

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="storage">Resolves the job storage</param>
    public HangfireSecretSweepTenantLock(Func<JobStorage> storage)
    {
        _storage = storage;
    }

    /// <inheritdoc />
    public IDisposable? TryAcquire(string tenantId, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var resource = ResourcePrefix + tenantId.ToLowerInvariant();
        var owner = new LockOwnerThread(_storage, resource, timeout);
        return owner.Start() ? owner : null;
    }

    /// <summary>
    ///     Holds one distributed lock on a dedicated thread: acquires it there, waits for the release signal and
    ///     releases it there.
    /// </summary>
    private sealed class LockOwnerThread : IDisposable
    {
        private readonly TaskCompletionSource<bool> _acquired =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _resource;
        private readonly Func<JobStorage> _storage;
        private readonly Thread _thread;
        private readonly TimeSpan _timeout;
        private int _disposed;

        public LockOwnerThread(Func<JobStorage> storage, string resource, TimeSpan timeout)
        {
            _storage = storage;
            _resource = resource;
            _timeout = timeout;
            _thread = new Thread(Run) { IsBackground = true, Name = "octo-secret-sweep-lock" };
        }

        /// <summary>
        ///     Starts the owner thread and waits for the acquisition. True when the lock is held.
        /// </summary>
        public bool Start()
        {
            _thread.Start();
            bool acquired;
            try
            {
                acquired = _acquired.Task.GetAwaiter().GetResult();
            }
            catch
            {
                _release.Dispose();
                throw;
            }

            if (!acquired)
            {
                _release.Dispose();
            }

            return acquired;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _release.Set();
            // The release is a single delete on the lock document; wait for it so the next sweep finds the
            // tenant free. A failed release surfaces here (as before), a hanging one does not block forever.
            if (_released.Task.Wait(ReleaseTimeout))
            {
                _released.Task.GetAwaiter().GetResult();
            }
        }

        private void Run()
        {
            IStorageConnection? connection = null;
            IDisposable? handle;
            try
            {
                connection = _storage().GetConnection();
                handle = connection.AcquireDistributedLock(_resource, _timeout);
            }
            catch (DistributedLockTimeoutException)
            {
                connection?.Dispose();
                _acquired.TrySetResult(false);
                return;
            }
            catch (Exception e)
            {
                connection?.Dispose();
                _acquired.TrySetException(e);
                return;
            }

            _acquired.TrySetResult(true);
            _release.Wait();
            _release.Dispose();
            try
            {
                try
                {
                    handle.Dispose();
                }
                finally
                {
                    connection.Dispose();
                }

                _released.TrySetResult();
            }
            catch (Exception e)
            {
                _released.TrySetException(e);
            }
        }
    }
}
