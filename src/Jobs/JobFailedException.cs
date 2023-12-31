namespace Meshmakers.Octo.Backend.Jobs;

/// <summary>
///     Represents an exception when a job fails
/// </summary>
[Serializable]
public class JobFailedException : Exception
{
    /// <inheritdoc />
    public JobFailedException()
    {
    }

    /// <inheritdoc />
    public JobFailedException(string message) : base(message)
    {
    }

    /// <inheritdoc />
    public JobFailedException(string message, Exception? inner) : base(message, inner)
    {
    }
}
