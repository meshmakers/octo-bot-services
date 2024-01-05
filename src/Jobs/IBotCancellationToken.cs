namespace Meshmakers.Octo.Backend.Jobs;

/// <summary>
///     Interface for bot cancellation handling
/// </summary>
public interface IBotCancellationToken
{
    /// <summary>
    ///     Returns the cancellation token
    /// </summary>
    CancellationToken ShutdownToken { get; }

    /// <summary>
    ///     Throws a <see cref="T:System.OperationCanceledException">OperationCanceledException</see> if
    ///     this token has had cancellation requested.
    /// </summary>
    /// <remarks>
    ///     This method provides functionality equivalent to:
    ///     <code>
    /// if (token.ShutdownToken.IsCancellationRequested)
    ///    throw new OperationCanceledException(token);
    /// </code>
    /// </remarks>
    /// <exception cref="T:System.OperationCanceledException">The token has had cancellation requested.</exception>
    void ThrowIfCancellationRequested();
}