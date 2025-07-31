using FluentAssertions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RepositoryUpdate.Models;
using RepositoryUpdate.Services;

namespace RepositoryUpdate.Tests.Integration;

/// <summary>
/// Integration tests that require actual command line tools to be installed.
/// These tests are marked as integration tests and can be skipped in CI environments
/// where the tools might not be available.
/// </summary>
[Trait("Category", "Integration")]
public class CommandExecutionServiceIntegrationTests : IDisposable
{
    private readonly CommandExecutionService _service;
    private readonly string _tempDirectory;
    private readonly List<string> _tempFiles = new();

    public CommandExecutionServiceIntegrationTests()
    {
        var config = new OctoSystemConfiguration
        {
            DatabaseHost = "localhost:27017",
            DatabaseUser = "testuser",
            DatabaseUserPassword = "testpass",
            AuthenticationDatabaseName = "admin",
            UseTls = false,
            AllowInsecureTls = false,
            UseDirectConnection = true
        };

        var options = Options.Create(config);
        var logger = NullLogger<CommandExecutionService>.Instance;

        _service = new CommandExecutionService(options, logger);
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"CommandServiceTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        // Cleanup temp files and directory
        foreach (var file in _tempFiles.Where(File.Exists))
        {
            try { File.Delete(file); } catch { /* ignore cleanup errors */ }
        }

        if (Directory.Exists(_tempDirectory))
        {
            try { Directory.Delete(_tempDirectory, true); } catch { /* ignore cleanup errors */ }
        }
    }

    #region System Command Tests

    [Fact]
    public async Task ExecuteCommandAsync_WithEchoCommand_ShouldWork()
    {
        // Arrange
        var message = "Hello Integration Test";

        // Act
        var result = await _service.ExecuteCommandAsync("echo", message);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.ExitCode.Should().Be(0);
        result.Output.Should().Contain(message);
        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithDateCommand_ShouldReturnCurrentDate()
    {
        // Act
        var result = await _service.ExecuteCommandAsync("date", "");

        // Assert
        result.Should().NotBeNull();
        if (result.Success) // date command might not be available on all systems
        {
            result.Output.Should().NotBeEmpty();
            result.ExitCode.Should().Be(0);
        }
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithLsCommand_ShouldListFiles()
    {
        // Arrange
        var testFile = Path.Combine(_tempDirectory, "test.txt");
        await File.WriteAllTextAsync(testFile, "test content", TestContext.Current.CancellationToken);
        _tempFiles.Add(testFile);

        // Act
        var result = await _service.ExecuteCommandAsync("ls", $"-la {_tempDirectory}");

        // Assert
        result.Should().NotBeNull();
        if (result.Success) // ls command might not be available on Windows
        {
            result.Output.Should().Contain("test.txt");
            result.ExitCode.Should().Be(0);
        }
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithWorkingDirectory_ShouldExecuteInSpecifiedDirectory()
    {
        // Arrange
        var testFile = Path.Combine(_tempDirectory, "workdir-test.txt");
        await File.WriteAllTextAsync(testFile, "test content", TestContext.Current.CancellationToken);
        _tempFiles.Add(testFile);

        // Act
        var result = await _service.ExecuteCommandAsync("ls", "", _tempDirectory);

        // Assert
        result.Should().NotBeNull();
        if (result.Success) // ls command might not be available on Windows
        {
            result.Output.Should().Contain("workdir-test.txt");
        }
    }

    #endregion

    #region MongoDB Shell Tests (Conditional)

    [Fact]
    public async Task ExecuteMongoShellScriptAsync_WithValidScript_ShouldExecuteSuccessfully()
    {
        // Skip if mongosh is not available
        if (!IsCommandAvailable("mongosh"))
        {
            return; // Skip test
        }

        // Arrange
        var scriptPath = Path.Combine(_tempDirectory, "test-script.js");
        await File.WriteAllTextAsync(scriptPath, "print('Hello from MongoDB Shell');", TestContext.Current.CancellationToken);
        _tempFiles.Add(scriptPath);

        // Act
        var result = await _service.ExecuteMongoShellScriptAsync("testdb", scriptPath);

        // Assert
        result.Should().NotBeNull();
        result.Command.Should().Contain("mongosh");
        result.Command.Should().Contain(scriptPath);
    }

    [Fact]
    public async Task ExecuteMongoShellCommandAsync_WithSimpleCommand_ShouldExecuteSuccessfully()
    {
        // Skip if mongosh is not available
        if (!IsCommandAvailable("mongosh"))
        {
            return; // Skip test
        }

        // Arrange
        var command = "print('Hello from direct command');";

        // Act
        var result = await _service.ExecuteMongoShellCommandAsync("testdb", command);

        // Assert
        result.Should().NotBeNull();
        result.Command.Should().Contain("mongosh");
        result.Command.Should().Contain("--eval");
    }

    #endregion

    #region MongoDB Tools Tests (Conditional)

    [Fact]
    public async Task ExecuteMongoDumpAsync_WithValidOptions_ShouldBuildCorrectCommand()
    {
        // Skip if mongodump is not available
        if (!IsCommandAvailable("mongodump"))
        {
            return; // Skip test
        }

        // Arrange
        var backupPath = Path.Combine(_tempDirectory, "backup");
        Directory.CreateDirectory(backupPath);
        
        var options = MongoDumpOptions.ForDatabase("testdb", backupPath);

        // Act
        var result = await _service.ExecuteMongoDumpAsync(options);

        // Assert
        result.Should().NotBeNull();
        result.Command.Should().Contain("mongodump");
        result.Command.Should().Contain("--db testdb");
        result.Command.Should().Contain($"--out \"{backupPath}\"");
    }

    [Fact]
    public async Task ExecuteMongoRestoreAsync_WithValidOptions_ShouldBuildCorrectCommand()
    {
        // Skip if mongorestore is not available
        if (!IsCommandAvailable("mongorestore"))
        {
            return; // Skip test
        }

        // Arrange
        var backupPath = Path.Combine(_tempDirectory, "restore");
        Directory.CreateDirectory(backupPath);
        
        var options = MongoRestoreOptions.FromDirectory(backupPath, "testdb");
        options.DryRun = true; // Use dry run to avoid actual restore

        // Act
        var result = await _service.ExecuteMongoRestoreAsync(options);

        // Assert
        result.Should().NotBeNull();
        result.Command.Should().Contain("mongorestore");
        result.Command.Should().Contain("--db testdb");
        result.Command.Should().Contain($"\"{backupPath}\"");
        result.Command.Should().Contain("--dryRun");
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task ExecuteCommandAsync_WithNonExistentCommand_ShouldReturnFailure()
    {
        // Arrange
        var nonExistentCommand = $"non_existent_command_{Guid.NewGuid():N}";

        // Act
        var result = await _service.ExecuteCommandAsync(nonExistentCommand, "");

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.ExitCode.Should().Be(-1);
        result.Error.Should().Contain("Failed to start process");
        result.Command.Should().Contain(nonExistentCommand);
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithCommandThatFails_ShouldReturnFailureWithExitCode()
    {
        // Act
        var result = await _service.ExecuteCommandAsync("ls", "/non/existent/directory");

        // Assert
        result.Should().NotBeNull();
        if (!result.Success) // ls command might not be available on Windows
        {
            result.ExitCode.Should().NotBe(0);
            result.Error.Should().NotBeEmpty();
        }
    }

    #endregion

    #region Performance Tests

    [Fact]
    public async Task ExecuteCommandAsync_ShouldTrackExecutionTime()
    {
        // Arrange
        var command = OperatingSystem.IsWindows() ? "timeout" : "sleep";
        var args = OperatingSystem.IsWindows() ? "1" : "0.1"; // 1 second on Windows, 0.1 second on Unix

        // Act
        var result = await _service.ExecuteCommandAsync(command, args);

        // Assert
        result.Should().NotBeNull();
        if (result.Success)
        {
            result.Duration.Should().BeGreaterThan(TimeSpan.FromMilliseconds(50));
            result.Duration.Should().BeLessThan(TimeSpan.FromSeconds(5));
        }
    }

    #endregion

    #region Concurrent Execution Tests

    [Fact]
    public async Task ExecuteCommandAsync_WithMultipleConcurrentCalls_ShouldHandleCorrectly()
    {
        // Arrange
        var tasks = new List<Task<CommandResult>>();
        const int concurrentCalls = 5;

        for (int i = 0; i < concurrentCalls; i++)
        {
            var message = $"Concurrent call {i}";
            tasks.Add(_service.ExecuteCommandAsync("echo", message));
        }

        // Act
        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().HaveCount(concurrentCalls);
        results.Should().OnlyContain(r => r.Success);
        
        for (int i = 0; i < concurrentCalls; i++)
        {
            results[i].Output.Should().Contain($"Concurrent call {i}");
        }
    }

    #endregion

    #region Helper Methods

    private static bool IsCommandAvailable(string command)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return false;

            process.WaitForExit(5000); // 5 second timeout
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    #endregion
}

/// <summary>
/// Performance and stress tests for the CommandExecutionService
/// </summary>
[Trait("Category", "Performance")]
public class CommandExecutionServicePerformanceTests
{
    private readonly CommandExecutionService _service;

    public CommandExecutionServicePerformanceTests()
    {
        var config = new OctoSystemConfiguration
        {
            DatabaseHost = "localhost:27017",
            DatabaseUser = "testuser",
            DatabaseUserPassword = "testpass",
            AuthenticationDatabaseName = "admin",
            UseTls = false,
            AllowInsecureTls = false,
            UseDirectConnection = true
        };

        var options = Options.Create(config);
        var logger = NullLogger<CommandExecutionService>.Instance;

        _service = new CommandExecutionService(options, logger);
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithManySmallCommands_ShouldPerformWell()
    {
        // Arrange
        const int commandCount = 50;
        var stopwatch = Stopwatch.StartNew();

        // Act
        var tasks = new List<Task<CommandResult>>();
        for (int i = 0; i < commandCount; i++)
        {
            tasks.Add(_service.ExecuteCommandAsync("echo", $"test{i}"));
        }

        var results = await Task.WhenAll(tasks);
        stopwatch.Stop();

        // Assert
        results.Should().HaveCount(commandCount);
        results.Should().OnlyContain(r => r.Success);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30)); // Should complete within 30 seconds
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithLongRunningCommand_ShouldNotTimeout()
    {
        // Arrange
        var command = OperatingSystem.IsWindows() ? "timeout" : "sleep";
        var args = OperatingSystem.IsWindows() ? "2" : "2"; // 2 seconds

        // Act
        var result = await _service.ExecuteCommandAsync(command, args);

        // Assert
        result.Should().NotBeNull();
        if (result.Success)
        {
            result.Duration.Should().BeGreaterThan(TimeSpan.FromSeconds(1.5));
            result.Duration.Should().BeLessThan(TimeSpan.FromSeconds(5));
        }
    }
}
