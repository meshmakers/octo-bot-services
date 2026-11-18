using Hangfire;
using Hangfire.Storage;

namespace Meshmakers.Octo.Backend.Jobs.Services;

/// <summary>
///     Holds one Hangfire distributed lock of the job storage (shared by every bot replica) on a dedicated thread:
///     acquires it there, waits for the release signal and releases it there.
/// </summary>
/// <remarks>
///     AB#5539: the Hangfire.Mongo lock is thread-affine - it counts acquisitions in a thread-local dictionary, and a
///     <c>Dispose</c> on another thread neither deletes the lock document nor clears that count. An async caller resumes
///     on any thread, so acquisition and release both happen on the owner thread. The storage keeps the lock alive with
///     a heartbeat while it is held; a killed process leaves it to expire (Hangfire.Mongo <c>DistributedLockLifetime</c>).
///     Used by the secret sweep tenant lock and the index upgrade (AB#9118).
/// </remarks>
internal sealed class HangfireDistributedLockOwner : IDisposable
{
    /// <summary>How long <see cref="Dispose" /> waits for the release to finish.</summary>
    internal static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource<bool> _acquired =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly ManualResetEventSlim _release = new(false);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _resource;
    private readonly Func<JobStorage> _storage;
    private readonly Thread _thread;
    private readonly TimeSpan _timeout;
    private int _disposed;

    public HangfireDistributedLockOwner(Func<JobStorage> storage, string resource, TimeSpan timeout,
        string threadName)
    {
        _storage = storage;
        _resource = resource;
        _timeout = timeout;
        _thread = new Thread(Run) { IsBackground = true, Name = threadName };
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
        // The release is a single delete on the lock document; wait for it so the next holder finds the
        // resource free. A failed release surfaces here (as before), a hanging one does not block forever.
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
