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
    /// <summary>How long one attempt to take the tenant lease waits; the yield request is renewed after each.</summary>
    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<IDisposable> EnterAsync(string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var settings = options.Value;
        if (settings.Schedule == IndexUpgradeSchedule.Window && !settings.Window.Enabled)
        {
            // No upgrade builds (production default until the go): nothing to wait for, no storage writes.
            return NoHold.Instance;
        }

        var started = _time.GetTimestamp();
        var waitLogged = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                coordination.RequestYield(tenantId);
                var lease = await Task.Run(() => coordination.TryAcquireTenant(tenantId, AttemptTimeout),
                    cancellationToken);
                if (lease != null)
                {
                    if (waitLogged)
                    {
                        logger.LogInformation(
                            "CK import of tenant '{TenantId}' continues after the index upgrade stopped ({WaitedMs} ms)",
                            tenantId, (long)_time.GetElapsedTime(started).TotalMilliseconds);
                    }

                    return new Held(this, tenantId, lease);
                }

                if (!waitLogged)
                {
                    logger.LogInformation(
                        "CK import of tenant '{TenantId}' waits for the running index upgrade to stop before its next collection",
                        tenantId);
                    waitLogged = true;
                }

                if (_time.GetElapsedTime(started) >= options.Value.ImportWaitTimeout)
                {
                    logger.LogWarning(
                        "CK import of tenant '{TenantId}' stopped waiting for the index upgrade after {Timeout}; it runs now " +
                        "and waits for the tenant's index_update_lock in the engine",
                        tenantId, options.Value.ImportWaitTimeout);
                    Clear(tenantId);
                    return NoHold.Instance;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Clear(tenantId);
            throw;
        }
        catch (Exception e)
        {
            // The coordination is a courtesy to the import: if the job storage cannot be reached, import as before.
            logger.LogWarning(e, "CK import of tenant '{TenantId}': could not coordinate with the index upgrade",
                tenantId);
            Clear(tenantId);
            return NoHold.Instance;
        }
    }

    private void Clear(string tenantId)
    {
        try
        {
            coordination.ClearYield(tenantId);
        }
        catch (Exception e)
        {
            // The request expires by itself (IIndexUpgradeCoordination.YieldRequestLifetime).
            logger.LogDebug(e, "Could not clear the yield request of tenant '{TenantId}'", tenantId);
        }
    }

    private sealed class Held(CkImportIndexUpgradeGate gate, string tenantId, IDisposable lease) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            gate.Clear(tenantId);
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
