using Hangfire;
using Meshmakers.Octo.Backend.Jobs.Services;

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
///     dedicated thread per held lock (<see cref="HangfireDistributedLockOwner" />).
/// </remarks>
public class HangfireSecretSweepTenantLock : ISecretSweepTenantLock
{
    internal const string ResourcePrefix = "octo:secret-sweep:lock:";

    /// <summary>
    ///     How long <see cref="IDisposable.Dispose" /> of a held lock waits for the release to finish.
    /// </summary>
    internal static readonly TimeSpan ReleaseTimeout = HangfireDistributedLockOwner.ReleaseTimeout;

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
        var owner = new HangfireDistributedLockOwner(_storage, resource, timeout, "octo-secret-sweep-lock");
        return owner.Start() ? owner : null;
    }
}
