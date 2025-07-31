using FluentAssertions;
using RepositoryUpdate.Models;

namespace RepositoryUpdate.Tests.Models;

public class CommandResultTests
{
    [Fact]
    public void CommandResult_WithDefaultValues_ShouldHaveExpectedDefaults()
    {
        // Arrange & Act
        var result = new CommandResult();

        // Assert
        result.Success.Should().BeFalse();
        result.ExitCode.Should().Be(0);
        result.Output.Should().BeEmpty();
        result.Error.Should().BeEmpty();
        result.Duration.Should().Be(TimeSpan.Zero);
        result.Command.Should().BeEmpty();
    }

    [Fact]
    public void CommandResult_Failure_ShouldCreateFailureResult()
    {
        // Arrange
        var errorMessage = "Something went wrong";

        // Act
        var result = CommandResult.Failure(errorMessage);

        // Assert
        result.Success.Should().BeFalse();
        result.ExitCode.Should().Be(-1);
        result.Error.Should().Be(errorMessage);
        result.Output.Should().BeEmpty();
        result.Duration.Should().Be(TimeSpan.Zero);
        result.Command.Should().BeEmpty();
    }

    [Fact]
    public void CommandResult_WithCommand_ShouldSetCommandAndReturnSelf()
    {
        // Arrange
        var result = new CommandResult();
        var command = "mongosh test.js";

        // Act
        var returnedResult = result.WithCommand(command);

        // Assert
        returnedResult.Should().BeSameAs(result);
        result.Command.Should().Be(command);
    }

    [Fact]
    public void CommandResult_ToString_WithOutputAndError_ShouldIncludeAllInfo()
    {
        // Arrange
        var result = new CommandResult
        {
            Success = true,
            ExitCode = 0,
            Output = "Success output",
            Error = "Warning message",
            Duration = TimeSpan.FromMilliseconds(1500),
            Command = "mongosh test.js"
        };

        // Act
        var stringResult = result.ToString();

        // Assert
        stringResult.Should().Contain("Command: mongosh test.js");
        stringResult.Should().Contain("Success: True");
        stringResult.Should().Contain("Exit Code: 0");
        stringResult.Should().Contain("Duration: 1500ms");
        stringResult.Should().Contain("Output:");
        stringResult.Should().Contain("Success output");
        stringResult.Should().Contain("Error:");
        stringResult.Should().Contain("Warning message");
    }

    [Fact]
    public void CommandResult_ToString_WithoutOutputAndError_ShouldNotIncludeEmptySections()
    {
        // Arrange
        var result = new CommandResult
        {
            Success = true,
            ExitCode = 0,
            Duration = TimeSpan.FromMilliseconds(500),
            Command = "echo test"
        };

        // Act
        var stringResult = result.ToString();

        // Assert
        stringResult.Should().Contain("Command: echo test");
        stringResult.Should().Contain("Success: True");
        stringResult.Should().Contain("Exit Code: 0");
        stringResult.Should().Contain("Duration: 500ms");
        stringResult.Should().NotContain("Output:");
        stringResult.Should().NotContain("Error:");
    }

    [Fact]
    public void CommandResult_ToString_WithOnlyOutput_ShouldIncludeOnlyOutput()
    {
        // Arrange
        var result = new CommandResult
        {
            Success = true,
            ExitCode = 0,
            Output = "Only output here",
            Duration = TimeSpan.FromMilliseconds(200),
            Command = "ls -la"
        };

        // Act
        var stringResult = result.ToString();

        // Assert
        stringResult.Should().Contain("Output:");
        stringResult.Should().Contain("Only output here");
        stringResult.Should().NotContain("Error:");
    }

    [Fact]
    public void CommandResult_ToString_WithOnlyError_ShouldIncludeOnlyError()
    {
        // Arrange
        var result = new CommandResult
        {
            Success = false,
            ExitCode = 1,
            Error = "Only error here",
            Duration = TimeSpan.FromMilliseconds(100),
            Command = "invalid-command"
        };

        // Act
        var stringResult = result.ToString();

        // Assert
        stringResult.Should().Contain("Error:");
        stringResult.Should().Contain("Only error here");
        stringResult.Should().NotContain("Output:");
    }
}
