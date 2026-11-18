using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.Jobs.IndexUpgrade;

/// <summary>
///     Lets a CK import go before the index upgrade of its tenant (AB#9118, AC "no clash with CK import"). The engine
///     holds the tenant's <c>index_update_lock</c> for a whole upgrade run, and a CK import waits for that lock only
///     90 s before it fails. So a CK import job of this service first asks a running upgrade to stop before its next
///     collection and waits for the tenant lease; the upgrade is scheduled again and continues after the import.
/// </summary>
public interface ICkImportIndexUpgradeGate
{
    /// <summary>
    ///     Waits until no index upgrade of <paramref name="tenantId" /> runs and holds it off until the returned handle
    ///     is disposed. Gives up after <see cref="IndexUpgradeOptions.ImportWaitTimeout" /> and returns a handle that
    ///     holds nothing; never throws except for cancellation.
    /// </summary>
    Task<IDisposable> EnterAsync(string tenantId, CancellationToken cancellationToken);
}

/// <inheritdoc />
public class CkImportIndexUpgradeGate(
    IIndexUpgradeCoordination coordination,
    IOptions<IndexUpgradeOptions> options,
    ILogger<CkImportIndexUpgradeGate> logger,
    TimeProvider? timeProvider = null) : ICkImportIndexUpgradeGate
{
    /// <summary>How long one attempt to take the tenant lease waits at most; the yield request is renewed after each.</summary>
    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<IDisposable> EnterAsync(string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var requestId = Guid.NewGuid().ToString("N");
        var started = _time.GetTimestamp();
        var waitLogged = false;
        try
        {
            // Read inside the try: a configuration value the binder cannot convert must not fail the CK import.
            var settings = options.Value;
            if (!settings.CanBuild)
            {
                // No upgrade builds (production default until the go): nothing to wait for, no storage writes.
                return NoHold.Instance;
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var budget = settings.ImportWaitTimeout - _time.GetElapsedTime(started);
                if (budget <= TimeSpan.Zero)
                {
                    logger.LogWarning(
                        "CK import of tenant '{TenantId}' stopped waiting for the index upgrade after {Timeout}; it runs now " +
                        "and waits for the tenant's index_update_lock in the engine",
                        tenantId, settings.ImportWaitTimeout);
                    Clear(tenantId, requestId);
                    return NoHold.Instance;
                }

                coordination.RequestYield(tenantId, requestId);
                budget = settings.ImportWaitTimeout - _time.GetElapsedTime(started);
                if (budget <= TimeSpan.Zero)
                {
                    continue; // the check at the top of the loop ends the wait
                }

                var attempt = budget < AttemptTimeout ? budget : AttemptTimeout;
                var lease = await Task.Run(() => coordination.TryAcquireTenant(tenantId, attempt), cancellationToken);
                if (lease != null)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        // The caller gave up while the attempt ran: do not hand out the lease.
                        lease.Dispose();
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    if (waitLogged)
                    {
                        logger.LogInformation(
                            "CK import of tenant '{TenantId}' continues after the index upgrade stopped ({WaitedMs} ms)",
                            tenantId, (long)_time.GetElapsedTime(started).TotalMilliseconds);
                    }

                    return new Held(this, tenantId, requestId, lease);
                }

                if (!waitLogged)
                {
                    logger.LogInformation(
                        "CK import of tenant '{TenantId}' waits for the running index upgrade to stop before its next collection",
                        tenantId);
                    waitLogged = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Clear(tenantId, requestId);
            throw;
        }
        catch (Exception e)
        {
            // The coordination is a courtesy to the import: if the job storage cannot be reached or the configuration
            // cannot be read, import as before.
            logger.LogWarning(e, "CK import of tenant '{TenantId}': could not coordinate with the index upgrade",
                tenantId);
            Clear(tenantId, requestId);
            return NoHold.Instance;
        }
    }

    private void Clear(string tenantId, string requestId)
    {
        try
        {
            coordination.ClearYield(tenantId, requestId);
        }
        catch (Exception e)
        {
            // The request expires by itself (IIndexUpgradeCoordination.YieldRequestLifetime).
            logger.LogDebug(e, "Could not clear the yield request of tenant '{TenantId}'", tenantId);
        }
    }

    private sealed class Held(CkImportIndexUpgradeGate gate, string tenantId, string requestId, IDisposable lease)
        : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            gate.Clear(tenantId, requestId);
            lease.Dispose();
        }
    }

    private sealed class NoHold : IDisposable
    {
        public static readonly NoHold Instance = new();

        public void Dispose()
        {
        }
    }
}
