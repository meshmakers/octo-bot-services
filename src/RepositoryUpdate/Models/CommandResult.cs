using System.Text;

namespace RepositoryUpdate.Models;

public class CommandResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public string Command { get; set; } = string.Empty;

    public static CommandResult Failure(string error)
    {
        return new CommandResult
        {
            Success = false,
            ExitCode = -1,
            Error = error
        };
    }

    public CommandResult WithCommand(string command)
    {
        Command = command;
        return this;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Command: {Command}");
        sb.AppendLine($"Success: {Success}");
        sb.AppendLine($"Exit Code: {ExitCode}");
        sb.AppendLine($"Duration: {Duration.TotalMilliseconds:F0}ms");

        if (!string.IsNullOrEmpty(Output))
        {
            sb.AppendLine("Output:");
            sb.AppendLine(Output);
        }

        if (!string.IsNullOrEmpty(Error))
        {
            sb.AppendLine("Error:");
            sb.AppendLine(Error);
        }

        return sb.ToString();
    }
}