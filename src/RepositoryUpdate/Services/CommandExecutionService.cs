using System.Diagnostics;
using System.Text;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using RepositoryUpdate.Models;

namespace RepositoryUpdate.Services;

public class CommandExecutionService(
    IOptions<OctoSystemConfiguration> systemConfigurationOptions,
    ILogger<CommandExecutionService> logger)
    : ICommandExecutionService
{
    private readonly Guid _instanceId = Guid.NewGuid();

    #region MongoDB Shell Operations

    public async Task<CommandResult> ExecuteMongoShellScriptAsync(string databaseName, string scriptPath,
        CancellationToken? cancellationToken = null)
    {
        var connectionString = GetConnectionString(databaseName);
        var args = BuildMongoShellArguments();
        if (!File.Exists(scriptPath))
        {
            return CommandResult.Failure($"Script file not found: {scriptPath}");
        }

        logger.LogInformation("Executing MongoDB shell script: {ScriptPath}", scriptPath);

        return await ExecuteCommandAsync("mongosh", $"{connectionString} {args} {scriptPath}", null, null, cancellationToken);
    }

    public async Task<CommandResult> ExecuteMongoShellCommandAsync(string databaseName, string command,
        CancellationToken? cancellationToken = null)
    {
        var connectionString = GetConnectionString(databaseName);
        var args = BuildMongoShellArguments();
        var escapedCommand = command.Replace("\"", "\\\"");

        logger.LogInformation("Executing MongoDB shell command: {Command}", command);

        return await ExecuteCommandAsync("mongosh", $"{connectionString} {args} --eval \"{escapedCommand}\"", null, null,
            cancellationToken);
    }

    #endregion

    #region MongoDB Dump/Restore Operations

    public async Task<CommandResult> ExecuteMongoDumpAsync(MongoDumpOptions options,
        CancellationToken? cancellationToken = null)
    {
        var args = BuildMongoDumpArguments(options);

        logger.LogInformation("Executing mongodump with options: {Options}", args);

        return await ExecuteCommandAsync("mongodump", args, null, null, cancellationToken);
    }

    public async Task<CommandResult> ExecuteMongoRestoreAsync(MongoRestoreOptions options, TimeSpan? timeout = null,
        CancellationToken? cancellationToken = null)
    {
        var args = BuildMongoRestoreArguments(options);

        logger.LogInformation("Executing mongorestore with options: {Options}", args);

        // Mongorestore kann hängen - Standard Timeout von 2 Minuten
        var restoreTimeout = timeout ?? TimeSpan.FromMinutes(2);
        return await ExecuteCommandAsync("mongorestore", args, timeout: restoreTimeout,
            cancellationToken: cancellationToken);
    }

    #endregion

    #region Core Command Execution

    public async Task<CommandResult> ExecuteCommandAsync(string fileName, string arguments,
        string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken? cancellationToken = null)
    {
        var timeoutMs = timeout?.TotalMilliseconds ?? 300000; // Default 5 Minuten

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };

        var output = new StringBuilder();
        var error = new StringBuilder();
        var startTime = DateTime.UtcNow;

        using var process = new Process();
        process.StartInfo = startInfo;

        // Event handlers for output capturing
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                output.AppendLine(e.Data);
                logger.LogDebug("STDOUT: {Output}", e.Data);

                // Bei MongoDB Tools: Info-Level für wichtige Meldungen
                if (fileName.StartsWith("mongo") && (e.Data.Contains("connected") ||
                                                     e.Data.Contains("done dumping") || e.Data.Contains("restored") ||
                                                     e.Data.Contains("finished") || e.Data.Contains("writing")))
                {
                    logger.LogInformation("MongoDB Tool Output: {Output}", e.Data);
                }
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                error.AppendLine(e.Data);
                logger.LogDebug("STDERR: {Error}", e.Data);

                // Bei MongoDB Tools: Warnings als Warning-Level
                if (fileName.StartsWith("mongo") && (e.Data.Contains("warning") ||
                                                     e.Data.Contains("error") || e.Data.Contains("failed")))
                {
                    logger.LogWarning("MongoDB Tool Warning/Error: {Error}", e.Data);
                }
            }
        };

        try
        {
            logger.LogDebug("Starting process: {FileName} {Arguments} (Timeout: {TimeoutMs}ms)",
                fileName, arguments, timeoutMs);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Warten mit Timeout
            var processTask = process.WaitForExitAsync(cancellationToken ?? CancellationToken.None);
            var timeoutTask = Task.Delay(TimeSpan.FromMilliseconds(timeoutMs));

            var completedTask = await Task.WhenAny(processTask, timeoutTask);

            var duration = DateTime.UtcNow - startTime;

            if (completedTask == timeoutTask)
            {
                // Timeout erreicht - Process beenden
                logger.LogWarning("Process timed out after {TimeoutMs}ms: {FileName} {Arguments}",
                    timeoutMs, fileName, arguments);

                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true); // Kill the entire process tree
                        await process.WaitForExitAsync(); // Wait for cleanup
                    }
                }
                catch (Exception killEx)
                {
                    logger.LogError(killEx, "Error killing timed out process");
                }

                return new CommandResult
                {
                    Success = false,
                    ExitCode = -1,
                    Output = output.ToString(),
                    Error = error + $"\nProcess timed out after {timeoutMs}ms",
                    Duration = duration,
                    Command = $"{fileName} {arguments}"
                };
            }

            // Process normal beendet
            await processTask; // Ensure the process is fully completed

            logger.LogDebug("Process completed. Exit code: {ExitCode}, Duration: {Duration}ms",
                process.ExitCode, duration.TotalMilliseconds);

            return new CommandResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = output.ToString(),
                Error = error.ToString(),
                Duration = duration,
                Command = $"{fileName} {arguments}"
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute command: {FileName} {Arguments}", fileName, arguments);

            return CommandResult.Failure($"Failed to start process '{fileName}': {ex.Message}")
                .WithCommand($"{fileName} {arguments}");
        }
    }

    #endregion

    #region Private methods

    private MongoUrl GetConnectionString(string databaseName)
    {
        var urlBuilder = new MongoUrlBuilder();

        var systemConfiguration = systemConfigurationOptions.Value;

        if (systemConfiguration.DatabaseHost.Contains(","))
        {
            urlBuilder.Servers =
                systemConfiguration.DatabaseHost.Split(",").Select(x => new MongoServerAddress(x));
        }
        else
        {
            urlBuilder.Server = new MongoServerAddress(systemConfiguration.DatabaseHost);
        }

        if (!string.IsNullOrWhiteSpace(systemConfiguration.DatabaseUser)
            && !string.IsNullOrWhiteSpace(systemConfiguration.DatabaseUserPassword))
        {
            urlBuilder.AuthenticationSource = systemConfiguration.AuthenticationDatabaseName;
        }

        urlBuilder.ApplicationName = $"OctoMeshUpdate-{databaseName}-{_instanceId}-{urlBuilder.Username}";
        urlBuilder.UseTls = systemConfiguration.UseTls;
        urlBuilder.AllowInsecureTls = systemConfiguration.AllowInsecureTls;
        urlBuilder.RetryReads = true;
        urlBuilder.RetryWrites = true;
        urlBuilder.DirectConnection = systemConfiguration.UseDirectConnection;

        return urlBuilder.ToMongoUrl();
    }

    private string BuildMongoDumpArguments(MongoDumpOptions options)
    {
        // Connection
        var args = new List<string>
        {
            $"--uri \"{GetConnectionString(options.Database)}\""
        };

        // Authentication
        if (!string.IsNullOrEmpty(systemConfigurationOptions.Value.AdminUser))
        {
            args.Add($"--username {systemConfigurationOptions.Value.AdminUser}");
        }

        if (!string.IsNullOrEmpty(systemConfigurationOptions.Value.AdminUserPassword))
        {
            args.Add($"--password {systemConfigurationOptions.Value.AdminUserPassword}");
        }

        // Database and Collection
        if (!string.IsNullOrEmpty(options.Database))
        {
            args.Add($"--db {options.Database}");
        }

        if (!string.IsNullOrEmpty(options.Collection))
        {
            args.Add($"--collection {options.Collection}");
        }

        // Output
        if (!string.IsNullOrEmpty(options.OutputDirectory))
        {
            args.Add($"--out \"{options.OutputDirectory}\"");
        }

        if (!string.IsNullOrEmpty(options.Archive))
        {
            args.Add($"--archive \"{options.Archive}\"");
        }

        // Options
        if (options.Gzip)
        {
            args.Add("--gzip");
        }

        if (options.Pretty)
        {
            args.Add("--pretty");
        }

        if (options.Verbose)
        {
            args.Add("--verbose");
        }

        return string.Join(" ", args);
    }

    private string BuildMongoShellArguments()
    {
        var args = new List<string>();
        // Authentication
        if (!string.IsNullOrEmpty(systemConfigurationOptions.Value.AdminUser))
        {
            args.Add($"--username={systemConfigurationOptions.Value.AdminUser}");
        }

        if (!string.IsNullOrEmpty(systemConfigurationOptions.Value.AdminUserPassword))
        {
            args.Add($"--password={systemConfigurationOptions.Value.AdminUserPassword}");
        }
        return string.Join(" ", args);
    }

    private string BuildMongoRestoreArguments(MongoRestoreOptions options)
    {
        // Connection
        var args = new List<string>
        {
            $"--uri=\"{GetConnectionString(options.Database)}\""
        };

        // Authentication
        if (!string.IsNullOrEmpty(systemConfigurationOptions.Value.AdminUser))
        {
            args.Add($"--username={systemConfigurationOptions.Value.AdminUser}");
        }

        if (!string.IsNullOrEmpty(systemConfigurationOptions.Value.AdminUserPassword))
        {
            args.Add($"--password={systemConfigurationOptions.Value.AdminUserPassword}");
        }

        // Database and Collection
        args.Add($"--nsInclude={options.Database}.{options.Collection}");

        // Input
        if (!string.IsNullOrEmpty(options.InputDirectory))
        {
            args.Add($"\"{options.InputDirectory}\"");
        }

        if (!string.IsNullOrEmpty(options.Archive))
        {
            args.Add($"--archive=\"{options.Archive}\"");
        }

        // Options
        if (options.Drop)
        {
            args.Add("--drop");
        }

        if (options.Gzip)
        {
            args.Add("--gzip");
        }

        if (options.Verbose)
        {
            args.Add("--verbose");
        }

        if (options.DryRun)
        {
            args.Add("--dryRun");
        }

        // Timeout und andere Optionen
        if (options.OplogReplay)
        {
            args.Add("--oplogReplay");
        }

        if (options.RestoreDbUsersAndRoles)
        {
            args.Add("--restoreDbUsersAndRoles");
        }

        if (options.NumParallelCollections.HasValue)
        {
            args.Add($"--numParallelCollections={options.NumParallelCollections}");
        }

        return string.Join(" ", args);
    }

    #endregion private methods
}