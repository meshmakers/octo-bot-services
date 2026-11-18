namespace Meshmakers.Octo.Backend.Jobs.IndexUpgrade;

/// <summary>When the index upgrade builds (AB#9118).</summary>
public enum IndexUpgradeSchedule
{
    /// <summary>
    ///     Build whenever a job is due: after the start of the service (<see cref="IndexUpgradeOptions.StartDelay" />)
    ///     and when a tenant is created or attached. test-2 and staging-1.
    /// </summary>
    Immediate,

    /// <summary>
    ///     Build only inside a window that <see cref="IndexUpgradeWindowOptions.Cron" /> opens for
    ///     <see cref="IndexUpgradeWindowOptions.MaxDuration" />, and only while
    ///     <see cref="IndexUpgradeWindowOptions.Enabled" /> is set. prod-1 and prod-2 (window off until the go).
    /// </summary>
    Window
}

/// <summary>Build window of the <see cref="IndexUpgradeSchedule.Window" /> schedule.</summary>
public class IndexUpgradeWindowOptions
{
    /// <summary>The window opens only when set. Default <c>false</c>: nothing is built.</summary>
    public bool Enabled { get; set; }

    /// <summary>When the window opens (Hangfire cron, UTC). Default 01:00 UTC daily.</summary>
    public string Cron { get; set; } = "0 1 * * *";

    /// <summary>
    ///     How long the window stays open. No collection is started after it closed; an index build already running
    ///     finishes. Default 4 hours.
    /// </summary>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromHours(4);
}

/// <summary>
///     Configuration of the paging index upgrade (section <c>IndexUpgrade</c>, environment variables
///     <c>OCTO_INDEXUPGRADE__*</c>; AB#9118).
/// </summary>
/// <remarks>
///     The code defaults are the safe production setting: <see cref="IndexUpgradeSchedule.Window" /> with the window
///     off, so a cluster without deployment values builds nothing. test-2 and staging-1 set
///     <c>Schedule = Immediate</c>.
/// </remarks>
public class IndexUpgradeOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "IndexUpgrade";

    /// <summary>When the upgrade builds. Default <see cref="IndexUpgradeSchedule.Window" />.</summary>
    public IndexUpgradeSchedule Schedule { get; set; } = IndexUpgradeSchedule.Window;

    /// <summary>The build window of <see cref="IndexUpgradeSchedule.Window" />.</summary>
    public IndexUpgradeWindowOptions Window { get; set; } = new();

    /// <summary>
    ///     How many tenants are rebuilt at the same time across all bot replicas. Default 1.
    /// </summary>
    public int MaxConcurrent { get; set; } = 1;

    /// <summary>
    ///     How long after the service start the sweep over all tenants runs (<see cref="IndexUpgradeSchedule.Immediate" />).
    ///     Gives the embedded CK model imports of all services at their tenant setup time to take and release the
    ///     tenant's <c>index_update_lock</c> first. Default 5 minutes.
    /// </summary>
    public TimeSpan StartDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     When a job that could not start (global limit reached, tenant busy with a CK import, <c>index_update_lock</c>
    ///     held) tries again. Every try adds its states to the job history in the dashboard. Default 5 minutes.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     How long a CK import job waits for a running upgrade of its tenant to stop before its next collection (the
    ///     index build in progress finishes first). After that the import runs anyway and may meet the 90 s lock wait
    ///     of the engine (then its retries). The wait holds a worker of the default queue and does not see a shutdown
    ///     (CK import jobs get no cancellation token). Default 30 minutes.
    /// </summary>
    public TimeSpan ImportWaitTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How often a running job checks for a yield request and the progress of its collections. Default 5 s.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
}
