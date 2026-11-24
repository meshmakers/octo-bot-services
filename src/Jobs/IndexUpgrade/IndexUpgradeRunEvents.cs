using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.IndexUpgrade;

/// <summary>Result of an index upgrade run as the run events report it (AB#9709).</summary>
public enum IndexUpgradeRunResult
{
    /// <summary>The run did all it could: every tenant checked (sweep), nothing the engine manages is left (tenant).</summary>
    Completed,

    /// <summary>The run ended with work left: a tenant not checked, a legacy index left, the window closed.</summary>
    Incomplete,

    /// <summary>The run failed with an exception.</summary>
    Failed
}

/// <summary>
///     The run events of the index upgrade (AB#9709): one start and one end line per sweep run, one end line per tenant
///     job. bot-services ships only <c>Warn</c> and above (nlog.config, <c>OCTO_LOG_LEVEL</c> unset or <c>Warn</c> on
///     every cluster), so these informational events are logged at <see cref="LogLevel.Warning" />
///     (<see cref="LogLevel.Error" /> for <see cref="IndexUpgradeRunResult.Failed" />) to reach Dash0 without changing a
///     log level. Every message starts with a fixed prefix, so a Dash0 query can find them by the body text; alert rules
///     that count warnings should exclude these prefixes. Per-index and per-collection details stay below Warning.
/// </summary>
public static class IndexUpgradeRunEvents
{
    /// <summary>Message prefix of the start of a sweep run.</summary>
    public const string RunStartedPrefix = "IndexUpgrade run started:";

    /// <summary>Message prefix of the end of a sweep run.</summary>
    public const string RunEndedPrefix = "IndexUpgrade run ended:";

    /// <summary>Message prefix of the end of a tenant job.</summary>
    public const string TenantEndedPrefix = "IndexUpgrade tenant ended:";

    /// <summary>Event id of the start of a sweep run.</summary>
    public static readonly EventId RunStarted = new(970901, "IndexUpgradeRunStarted");

    /// <summary>Event id of the end of a sweep run.</summary>
    public static readonly EventId RunEnded = new(970902, "IndexUpgradeRunEnded");

    /// <summary>Event id of the end of a tenant job.</summary>
    public static readonly EventId TenantEnded = new(970903, "IndexUpgradeTenantEnded");

    /// <summary>The level of an end event: Error for a failed run, Warning otherwise (the shipped level).</summary>
    public static LogLevel EndLevel(IndexUpgradeRunResult result) =>
        result == IndexUpgradeRunResult.Failed ? LogLevel.Error : LogLevel.Warning;

    /// <summary>
    ///     The result of a tenant job that ended with <paramref name="status" />; null for a status that is no end of the
    ///     job (<see cref="IndexUpgradeRunStatus.Running" />).
    /// </summary>
    public static IndexUpgradeRunResult? ResultOf(IndexUpgradeRunStatus status) => status switch
    {
        IndexUpgradeRunStatus.Succeeded or IndexUpgradeRunStatus.NothingToDo => IndexUpgradeRunResult.Completed,
        IndexUpgradeRunStatus.Failed => IndexUpgradeRunResult.Failed,
        IndexUpgradeRunStatus.Incomplete or IndexUpgradeRunStatus.Deferred or IndexUpgradeRunStatus.Interrupted =>
            IndexUpgradeRunResult.Incomplete,
        _ => null
    };
}
