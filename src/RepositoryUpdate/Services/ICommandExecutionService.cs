using RepositoryUpdate.Models;

namespace RepositoryUpdate.Services;

public interface ICommandExecutionService
{
    Task<CommandResult> ExecuteMongoShellScriptAsync(string databaseName, string scriptPath, CancellationToken? cancellationToken = null);
    Task<CommandResult> ExecuteMongoShellCommandAsync(string databaseName, string command, CancellationToken? cancellationToken = null);
    Task<CommandResult> ExecuteMongoDumpAsync(MongoDumpOptions options, CancellationToken? cancellationToken = null);
    Task<CommandResult> ExecuteMongoRestoreAsync(MongoRestoreOptions options, TimeSpan? timeout = null, CancellationToken? cancellationToken = null);
    Task<CommandResult> ExecuteCommandAsync(string fileName, string arguments, string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken? cancellationToken = null);
}