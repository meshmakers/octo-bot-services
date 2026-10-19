
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

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

    internal static Exception ContentTypeNotSupported(string contentType)
    {
        return new JobFailedException($"File type '{contentType}' is not supported.");
    }

    internal static Exception ExportCallerContextMissing(string tenantId, string exportKind)
    {
        return new JobFailedException(
            $"The {exportKind} export for tenant '{tenantId}' carries no caller identity (subject id) and is refused: " +
            "an export runs with the data permissions of the user who requested it and never as the system. " +
            "The request was sent by an older asset repository build. Update the asset repository and request the export again.");
    }

    internal static Exception CacheStreamNotFound(string tenantId, string key)
    {
        return new JobFailedException($"No value of key '{key} in distribute cache for tenant '{tenantId}' found.");
    }

    internal static Exception CommandExecutionFailed(CommandResult commandResult, string tenantId, string commandName)
    {
        return new JobFailedException(
            $"Command '{commandName}' failed for tenant '{tenantId}' with exit code {commandResult.ExitCode} and this details: {commandResult}");
    }
}