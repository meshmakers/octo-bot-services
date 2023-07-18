using System.Threading;
using Hangfire;
using Meshmakers.Octo.Common.Shared.Jobs;

namespace Meshmakers.Octo.Backend.Jobs;

/// <summary>
/// Cancellation token for bots 
/// </summary>
public class BotCancellationToken : IBotCancellationToken
{
    private readonly IJobCancellationToken _cancellationToken;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="cancellationToken"></param>
    public BotCancellationToken(IJobCancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
    }

    /// <inheritdoc />
    public CancellationToken ShutdownToken => _cancellationToken.ShutdownToken;


    /// <inheritdoc />
    public void ThrowIfCancellationRequested()
    {
        _cancellationToken.ThrowIfCancellationRequested();
    }
    
    /// <summary>
    /// Returns null
    /// </summary>
    public static IBotCancellationToken? Null => null;
}