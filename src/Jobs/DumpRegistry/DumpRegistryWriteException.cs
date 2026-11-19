namespace Meshmakers.Octo.Backend.Jobs.DumpRegistry;

/// <summary>Writing a dump registry entry failed on every attempt; the dump itself was readable.</summary>
[Serializable]
public sealed class DumpRegistryWriteException : JobFailedException
{
    /// <inheritdoc />
    public DumpRegistryWriteException(string message, Exception inner) : base(message, inner)
    {
    }
}
