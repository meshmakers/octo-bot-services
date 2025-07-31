using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using RepositoryUpdate.Services;

namespace RepositoryUpdate.Tests.Utilities;

/// <summary>
/// Test utilities and helper methods for CommandExecutionService tests
/// </summary>
public static class TestUtilities
{
    /// <summary>
    /// Creates a CommandExecutionService with default test configuration
    /// </summary>
    public static CommandExecutionService CreateTestService(
        OctoSystemConfiguration? config = null,
        ILogger<CommandExecutionService>? logger = null)
    {
        var testConfig = config ?? new OctoSystemConfiguration
        {
            DatabaseHost = "localhost:27017",
            DatabaseUser = "testuser",
            DatabaseUserPassword = "testpass",
            AuthenticationDatabaseName = "admin",
            UseTls = false,
            AllowInsecureTls = false,
            UseDirectConnection = true
        };

        var mockOptions = new Mock<IOptions<OctoSystemConfiguration>>();
        mockOptions.Setup(x => x.Value).Returns(testConfig);

        var mockLogger = logger ?? new Mock<ILogger<CommandExecutionService>>().Object;

        return new CommandExecutionService(mockOptions.Object, mockLogger);
    }

    /// <summary>
    /// Creates a temporary file with the specified content and returns the path
    /// </summary>
    public static async Task<string> CreateTempFileAsync(string content, string? extension = null)
    {
        var tempFile = Path.GetTempFileName();

        if (!string.IsNullOrEmpty(extension))
        {
            var newTempFile = Path.ChangeExtension(tempFile, extension);
            File.Move(tempFile, newTempFile);
            tempFile = newTempFile;
        }

        await File.WriteAllTextAsync(tempFile, content);
        return tempFile;
    }

    /// <summary>
    /// Creates a temporary directory and returns the path
    /// </summary>
    public static string CreateTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"TestDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    /// <summary>
    /// Safely deletes a file, ignoring errors
    /// </summary>
    public static void SafeDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    /// <summary>
    /// Safely deletes a directory, ignoring errors
    /// </summary>
    public static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    /// <summary>
    /// Checks if a command line tool is available on the system
    /// </summary>
    public static bool IsCommandAvailable(string command)
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

    /// <summary>
    /// Gets the appropriate echo command for the current operating system
    /// </summary>
    public static (string command, string args) GetEchoCommand(string message)
    {
        return OperatingSystem.IsWindows()
            ? ("cmd", $"/c echo {message}")
            : ("echo", message);
    }

    /// <summary>
    /// Gets the appropriate sleep command for the current operating system
    /// </summary>
    public static (string command, string args) GetSleepCommand(double seconds)
    {
        return OperatingSystem.IsWindows()
            ? ("timeout", $"/t {(int)Math.Ceiling(seconds)} /nobreak")
            : ("sleep", seconds.ToString("0.0"));
    }

    /// <summary>
    /// Gets the appropriate list directory command for the current operating system
    /// </summary>
    public static (string command, string args) GetListDirectoryCommand(string? directory = null)
    {
        var dir = directory ?? ".";
        return OperatingSystem.IsWindows()
            ? ("cmd", $"/c dir \"{dir}\"")
            : ("ls", $"-la \"{dir}\"");
    }

    /// <summary>
    /// Creates a mock IOptions<OctoSystemConfiguration> with the specified configuration
    /// </summary>
    public static IOptions<OctoSystemConfiguration> CreateMockOptions(OctoSystemConfiguration config)
    {
        var mock = new Mock<IOptions<OctoSystemConfiguration>>();
        mock.Setup(x => x.Value).Returns(config);
        return mock.Object;
    }

    /// <summary>
    /// Creates a mock logger for CommandExecutionService
    /// </summary>
    public static Mock<ILogger<CommandExecutionService>> CreateMockLogger()
    {
        return new Mock<ILogger<CommandExecutionService>>();
    }

    /// <summary>
    /// Verifies that a logger was called with a specific log level and message pattern
    /// </summary>
    public static void VerifyLogCalled(
        Mock<ILogger<CommandExecutionService>> mockLogger,
        LogLevel level,
        string messagePattern,
        Times? times = null)
    {
        var expectedTimes = times ?? Times.Once();

        mockLogger.Verify(
            x => x.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains(messagePattern)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            expectedTimes);
    }
}

/// <summary>
/// Custom test collection for tests that need to run sequentially
/// </summary>
[CollectionDefinition("Sequential")]
public class SequentialTestCollection : ICollectionFixture<SequentialTestCollection>
{
}

/// <summary>
/// Test fixture for managing temporary resources
/// </summary>
public class TempResourceFixture : IDisposable
{
    private readonly List<string> _tempFiles = new();
    private readonly List<string> _tempDirectories = new();

    public string CreateTempFile(string content, string? extension = null)
    {
        var tempFile = Path.GetTempFileName();

        if (!string.IsNullOrEmpty(extension))
        {
            var newTempFile = Path.ChangeExtension(tempFile, extension);
            File.Move(tempFile, newTempFile);
            tempFile = newTempFile;
        }

        File.WriteAllText(tempFile, content);
        _tempFiles.Add(tempFile);
        return tempFile;
    }

    public string CreateTempDirectory()
    {
        var tempDir = TestUtilities.CreateTempDirectory();
        _tempDirectories.Add(tempDir);
        return tempDir;
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            TestUtilities.SafeDeleteFile(file);
        }

        foreach (var directory in _tempDirectories)
        {
            TestUtilities.SafeDeleteDirectory(directory);
        }
    }
}