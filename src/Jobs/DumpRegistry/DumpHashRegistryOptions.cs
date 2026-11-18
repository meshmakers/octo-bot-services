namespace Meshmakers.Octo.Backend.Jobs.DumpRegistry;

/// <summary>Options of the dump registry (configuration section <see cref="SectionName" />).</summary>
public sealed class DumpHashRegistryOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "DumpHashRegistry";

    /// <summary>How long an entry admits a restore after the dump; default 365 days.</summary>
    public int RetentionDays { get; set; } = 365;

    /// <summary>The retention as a time span; at least one day.</summary>
    public TimeSpan Retention => TimeSpan.FromDays(Math.Max(1, RetentionDays));
}
