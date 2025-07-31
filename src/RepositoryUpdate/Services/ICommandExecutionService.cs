using RepositoryUpdate.Models;

namespace RepositoryUpdate.Services;

public interface ICommandExecutionService
{
    Task<CommandResult> ExecuteMongoShellScriptAsync(string databaseName, string scriptPath);
    Task<CommandResult> ExecuteMongoShellCommandAsync(string databaseName, string command);
    Task<CommandResult> ExecuteMongoDumpAsync(MongoDumpOptions options);
    Task<CommandResult> ExecuteMongoRestoreAsync(MongoRestoreOptions options, TimeSpan? timeout = null);
    Task<CommandResult> ExecuteCommandAsync(string fileName, string arguments, string? workingDirectory = null, TimeSpan? timeout = null);
}