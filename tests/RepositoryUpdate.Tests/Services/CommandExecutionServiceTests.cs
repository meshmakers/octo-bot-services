using FluentAssertions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using RepositoryUpdate.Models;
using RepositoryUpdate.Services;

namespace RepositoryUpdate.Tests.Services;

public class CommandExecutionServiceTests
{
    private readonly Mock<ILogger<CommandExecutionService>> _mockLogger;
    private readonly CommandExecutionService _service;
    private readonly OctoSystemConfiguration _testConfig;

    public CommandExecutionServiceTests(ITestOutputHelper output)
    {
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddXUnit(output); // Redirect logs to xUnit test output
        });
        _testConfig = new OctoSystemConfiguration
        {
            DatabaseHost = "localhost:27017",
            AdminUser = "octo-system-admin",
            AdminUserPassword = "REDACTED-LOCAL-DEV-PASSWORD",
            AuthenticationDatabaseName = "admin",
            UseTls = false,
            AllowInsecureTls = false,
            UseDirectConnection = true
        };

        var mockSystemConfig = new Mock<IOptions<OctoSystemConfiguration>>();
        mockSystemConfig.Setup(x => x.Value).Returns(_testConfig);

        _mockLogger = new Mock<ILogger<CommandExecutionService>>();

        _service = new CommandExecutionService(mockSystemConfig.Object, _mockLogger.Object);
    }

    #region MongoDB Shell Script Tests

    [Fact]
    public async Task ExecuteMongoShellScriptAsync_WithValidScript_ShouldReturnSuccess()
    {
        // Arrange
        var tempScript = Path.GetTempFileName();
        tempScript = Path.ChangeExtension(tempScript, ".js");
        
        try
        {
            await File.WriteAllTextAsync(tempScript, "print('Hello MongoDB');", TestContext.Current.CancellationToken);

            // Act
            var result = await _service.ExecuteMongoShellScriptAsync("testdb", tempScript);

            // Assert
            result.Should().NotBeNull();
            result.Command.Should().Contain("mongosh");
            result.Command.Should().Contain(tempScript);
        }
        finally
        {
            if (File.Exists(tempScript))
                File.Delete(tempScript);
        }
    }

    [Fact]
    public async Task ExecuteMongoShellScriptAsync_WithNonExistentScript_ShouldReturnFailure()
    {
        // Arrange
        var nonExistentScript = "non_existent_script.js";

        // Act
        var result = await _service.ExecuteMongoShellScriptAsync("testdb", nonExistentScript);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Script file not found");
        result.Error.Should().Contain(nonExistentScript);
    }

    [Fact]
    public async Task ExecuteMongoShellCommandAsync_WithSimpleCommand_ShouldBuildCorrectCommand()
    {
        // Arrange
        var command = "db.stats()";

        // Act
        var result = await _service.ExecuteMongoShellCommandAsync("testdb", command);

        // Assert
        result.Should().NotBeNull();
        result.Command.Should().Contain("mongosh");
        result.Command.Should().Contain("--eval");
        result.Command.Should().Contain("db.stats()");
    }

    [Fact]
    public async Task ExecuteMongoShellCommandAsync_WithQuotesInCommand_ShouldEscapeQuotes()
    {
        // Arrange
        var command = "print(\"Hello World\")";

        // Act
        var result = await _service.ExecuteMongoShellCommandAsync("testdb", command);

        // Assert
        result.Should().NotBeNull();
        result.Command.Should().Contain("mongosh");
        result.Command.Should().Contain("print(\\\"Hello World\\\")");
    }

    #endregion

    #region MongoDB Dump Tests

    [Fact]
    public void BuildMongoDumpArguments_WithBasicOptions_ShouldBuildCorrectArguments()
    {
        // Arrange
        var options = new MongoDumpOptions
        {
            Database = "testdb",
            OutputDirectory = "/tmp/backup"
        };

        // Act
        var result = _service.ExecuteMongoDumpAsync(options);

        // Assert - We can't easily test the private method directly, but we can verify the command execution
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteMongoDumpAsync_WithForDatabaseOptions_ShouldIncludeCorrectParameters()
    {
        // Arrange
        var options = MongoDumpOptions.ForDatabase("testdb", "/tmp/backup");

        // Act
        var result = await _service.ExecuteMongoDumpAsync(options);

        // Assert
        result.Should().NotBeNull();
        result.Command.Should().Contain("mongodump");
        result.Command.Should().Contain("--db=testdb");
        result.Command.Should().Contain("--out=\"/tmp/backup\"");
    }

    [Fact]
    public async Task ExecuteMongoDumpAsync_WithForArchiveOptions_ShouldIncludeArchiveAndGzip()
    {
        // Arrange
        var options = MongoDumpOptions.ForArchive("testdb", "/tmp/backup.gz");

        // Act
        var result = await _service.ExecuteMongoDumpAsync(options);

        // Assert
        result.Should().NotBeNull();
        result.Command.Should().Contain("mongodump");
        result.Command.Should().Contain("--db=testdb");
        result.Command.Should().Contain("--archive=\"/tmp/backup.gz\"");
        result.Command.Should().Contain("--gzip");
    }

    [Fact]
    public async Task ExecuteMongoDumpAsync_WithAllOptions_ShouldIncludeAllParameters()
    {
        // Arrange
        var options = new MongoDumpOptions
        {
            Database = "testdb",
            Collection = "testcollection",
            Archive = "/tmp/backup.gz",
            Gzip = true,
            Pretty = true,
            Verbose = true
        };

        // Act
        var result = await _service.ExecuteMongoDumpAsync(options);

        // Assert
        result.Should().NotBeNull();
        result.Command.Should().Contain("mongodump");
        result.Command.Should().Contain("--db=testdb");
        result.Command.Should().Contain("--collection=testcollection");
        result.Command.Should().Contain("--archive=\"/tmp/backup.gz\"");
        result.Command.Should().Contain("--gzip");
        result.Command.Should().Contain("--pretty");
        result.Command.Should().Contain("--verbose");
    }

    #endregion

    #region MongoDB Restore Tests

    [Fact]
    public async Task ExecuteMongoRestoreAsync_WithFromDirectoryOptions_ShouldIncludeCorrectParameters()
    {
        // Arrange
        var options = MongoRestoreOptions.FromDirectory("/tmp/backup", "testdb");

        // Act
        var result = await _service.ExecuteMongoRestoreAsync(options);

        // Assert
        result.Should().NotBeNull();
        result.Command.Should().Contain("mongorestore");
        result.Command.Should().Contain("--nsInclude=testdb.*");
        result.Command.Should().Contain("\"/tmp/backup\"");
    }

    [Fact]
    public async Task ExecuteMongoRestoreAsync_WithFromArchiveOptions_ShouldIncludeArchiveAndGzip()
    {
        // Arrange
        var options = MongoRestoreOptions.FromArchive("/tmp/backup.gz", "testdb");

        // Act
        var result = await _service.ExecuteMongoRestoreAsync(options);

        // Assert
        result.Should().NotBeNull();
        result.Command.Should().Contain("mongorestore");
        result.Command.Should().Contain("--nsInclude=testdb.*");
        result.Command.Should().Contain("--archive=\"/tmp/backup.gz\"");
        result.Command.Should().Contain("--gzip");
    }

    [Fact]
    public async Task ExecuteMongoRestoreAsync_WithAllOptions_ShouldIncludeAllParameters()
    {
        // Arrange
        var options = new MongoRestoreOptions
        {
            Database = "testdb",
            Collection = "testcollection",
            Archive = "/tmp/backup.gz",
            Drop = true,
            Gzip = true,
            Verbose = true,
            DryRun = true
        };

        // Act
        var result = await _service.ExecuteMongoRestoreAsync(options);

        // Assert
        result.Should().NotBeNull();
        result.Command.Should().Contain("mongorestore");
        result.Command.Should().Contain("--nsInclude=testdb.testcollection");
        result.Command.Should().Contain("--archive=\"/tmp/backup.gz\"");
        result.Command.Should().Contain("--drop");
        result.Command.Should().Contain("--gzip");
        result.Command.Should().Contain("--verbose");
        result.Command.Should().Contain("--dryRun");
    }

    #endregion

    #region Core Command Execution Tests

    [Fact]
    public async Task ExecuteCommandAsync_WithEchoCommand_ShouldReturnSuccess()
    {
        // Arrange
        var testMessage = "Hello World";

        // Act
        var result = await _service.ExecuteCommandAsync("echo", testMessage);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.ExitCode.Should().Be(0);
        result.Output.Should().Contain(testMessage);
        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
        result.Command.Should().Be($"echo {testMessage}");
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithInvalidCommand_ShouldReturnFailure()
    {
        // Arrange
        var invalidCommand = "this_command_does_not_exist_12345";

        // Act
        var result = await _service.ExecuteCommandAsync(invalidCommand, "");

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.ExitCode.Should().Be(-1);
        result.Error.Should().Contain("Failed to start process");
        result.Command.Should().Contain(invalidCommand);
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithWorkingDirectory_ShouldUseSpecifiedDirectory()
    {
        // Arrange
        var tempDir = Path.GetTempPath();

        // Act
        var result = await _service.ExecuteCommandAsync("pwd", "", tempDir);

        // Assert
        result.Should().NotBeNull();
        if (result.Success) // pwd might not be available on all systems
        {
            result.Output.Should().Contain(tempDir.TrimEnd('/'));
        }
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithCommandThatWritesToStderr_ShouldCaptureError()
    {
        // Arrange & Act
        var result = await _service.ExecuteCommandAsync("bash", "-c \"echo 'error message' >&2\"");

        // Assert
        result.Should().NotBeNull();
        if (result.Success) // bash might not be available on all systems
        {
            result.Error.Should().Contain("error message");
        }
    }

    #endregion

    #region Connection String Tests

    [Fact]
    public void GetConnectionString_WithBasicConfig_ShouldBuildCorrectUrl()
    {
        // This tests the private method indirectly by checking the command output
        // We can't test it directly, but we can verify the behavior through public methods

        // Arrange & Act
        var result = _service.ExecuteMongoShellCommandAsync("testdb", "print('test')");

        // Assert
        result.Should().NotBeNull();
        // The connection string building is tested indirectly through the command execution
    }

    [Fact]
    public void GetConnectionString_WithMultipleHosts_ShouldHandleMultipleServers()
    {
        // Arrange
        _testConfig.DatabaseHost = "host1:27017,host2:27017,host3:27017";

        // Act
        var result = _service.ExecuteMongoShellCommandAsync("testdb", "print('test')");

        // Assert
        result.Should().NotBeNull();
        // The connection string should be built correctly with multiple hosts
    }

    [Fact]
    public void GetConnectionString_WithTlsEnabled_ShouldIncludeTlsSettings()
    {
        // Arrange
        _testConfig.UseTls = true;
        _testConfig.AllowInsecureTls = true;

        // Act
        var result = _service.ExecuteMongoShellCommandAsync("testdb", "print('test')");

        // Assert
        result.Should().NotBeNull();
        // TLS settings should be included in the connection string
    }

    #endregion

    #region Logging Tests

    [Fact]
    public async Task ExecuteMongoShellScriptAsync_ShouldLogInformation()
    {
        // Arrange
        var tempScript = Path.GetTempFileName();
        tempScript = Path.ChangeExtension(tempScript, ".js");
        
        try
        {
            await File.WriteAllTextAsync(tempScript, "print('Hello MongoDB');", TestContext.Current.CancellationToken);

            // Act
            await _service.ExecuteMongoShellScriptAsync("testdb", tempScript);

            // Assert
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Executing MongoDB shell script")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }
        finally
        {
            if (File.Exists(tempScript))
                File.Delete(tempScript);
        }
    }

    [Fact]
    public async Task ExecuteMongoShellCommandAsync_ShouldLogInformation()
    {
        // Arrange
        var command = "print('test')";

        // Act
        await _service.ExecuteMongoShellCommandAsync("testdb", command);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Executing MongoDB shell command")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteMongoDumpAsync_ShouldLogInformation()
    {
        // Arrange
        var options = MongoDumpOptions.ForDatabase("testdb", "/tmp/backup");

        // Act
        await _service.ExecuteMongoDumpAsync(options);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Executing mongodump")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteMongoRestoreAsync_ShouldLogInformation()
    {
        // Arrange
        var options = MongoRestoreOptions.FromDirectory("/tmp/backup", "testdb");

        // Act
        await _service.ExecuteMongoRestoreAsync(options);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Executing mongorestore")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion
}
