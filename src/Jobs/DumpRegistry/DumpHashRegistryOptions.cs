namespace Meshmakers.Octo.Backend.Jobs.DumpRegistry;

/// <summary>Options of the dump registry (configuration section <see cref="SectionName" />).</summary>
public sealed class DumpHashRegistryOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "DumpHashRegistry";

    /// <summary>The longest retention that is honoured; larger values are cut to it.</summary>
    public const int MaxRetentionDays = 36500;

    /// <summary>How long an entry admits a restore after the dump; default 365 days.</summary>
    public int RetentionDays { get; set; } = 365;

    /// <summary>The retention as a time span; between one day and <see cref="MaxRetentionDays" />.</summary>
    public TimeSpan Retention => TimeSpan.FromDays(Math.Clamp(RetentionDays, 1, MaxRetentionDays));
}
