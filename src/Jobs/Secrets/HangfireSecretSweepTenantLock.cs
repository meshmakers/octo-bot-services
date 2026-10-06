using Hangfire;
using Hangfire.Storage;

namespace Meshmakers.Octo.Backend.Jobs.Secrets;

/// <summary>
///     <see cref="ISecretSweepTenantLock" /> on a Hangfire distributed lock of the job storage, so it holds
///     across bot replicas.
/// </summary>
public class HangfireSecretSweepTenantLock : ISecretSweepTenantLock
{
    internal const string ResourcePrefix = "octo:secret-sweep:lock:";

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
        var connection = _storage().GetConnection();
        try
        {
            var handle = connection.AcquireDistributedLock(ResourcePrefix + tenantId.ToLowerInvariant(), timeout);
            return new Held(handle, connection);
        }
        catch (DistributedLockTimeoutException)
        {
            connection.Dispose();
            return null;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private sealed class Held(IDisposable handle, IStorageConnection connection) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                handle.Dispose();
            }
            finally
            {
                connection.Dispose();
            }
        }
    }
}
