using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
///     Reads the tenant notification warnings of an engine restore or backup result. After its commit the engine publishes
///     the tenant-created notification (attach) and the tenant-deleted notification (drop, detach); a publish that fails
///     does not fail the operation. The engine then sets <see cref="CommandResult.TenantCreatedNotificationFailed" /> and
///     names the recovery in <see cref="CommandResult.Output" /> with a line that says "run ClearCache for the tenant".
///     The jobs pass these lines to the administrator in their result, so a restore that left the services without the
///     restored tenant does not look like a plain success.
/// </summary>
public static class TenantNotificationWarnings
{
    /// <summary>The recovery every warning names; the marker of a warning line in the engine output.</summary>
    public const string RecoveryCommand = "ClearCache";

    /// <summary>
    ///     The warnings of <paramref name="result" />: every line of its output that names <see cref="RecoveryCommand" />,
    ///     and a fallback text when the tenant-created notification failed without such a line.
    /// </summary>
    public static IReadOnlyList<string> From(CommandResult result, string tenantId)
    {
        var warnings = (result.Output ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Contains(RecoveryCommand, StringComparison.Ordinal))
            .ToList();

        if (result.TenantCreatedNotificationFailed && warnings.Count == 0)
        {
            warnings.Add(
                $"Tenant '{tenantId}' was restored, but the tenant-created notification failed. The services have not " +
                "set the tenant up: run ClearCache for the tenant.");
        }

        return warnings;
    }
}
