namespace Meshmakers.Octo.Backend.BotServices.Services.DumpRegistry;

/// <summary>
///     Runs an initialization once and shares it with concurrent callers; after a failure or cancellation the next call
///     runs it again instead of rethrowing the old failure forever.
/// </summary>
public sealed class RetryingInitialization(Func<Task> initialize)
{
    private readonly Lock _gate = new();
    private Task? _current;

    /// <summary>The running or completed initialization; a new one when the last one failed.</summary>
    public Task EnsureAsync()
    {
        lock (_gate)
        {
            var needsNewRun = _current == null || _current.IsFaulted || _current.IsCanceled;
            if (needsNewRun)
            {
                _current = initialize();
            }

            return _current!;
        }
    }
}
