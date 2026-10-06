namespace Meshmakers.Octo.Backend.Jobs.Secrets;

/// <summary>
///     Options of the secret sweep (AB#5539, concept AB#5528 §5.2 phases 4-5, §5.3, §6), bound from the
///     configuration section <c>Bot:SecretSweep</c> (environment: <c>OCTO_BOT__SECRETSWEEP__*</c>).
/// </summary>
/// <remarks>
///     The key ring itself is NOT configured here: it is the engine's <c>SecretEncryption</c> section
///     (<c>SecretEncryption:Keys:&lt;kid&gt;</c>, <c>SecretEncryption:ActiveKeyId</c>,
///     <c>SecretEncryption:LegacyV1Key</c>), bound by <c>AddRuntimeEngine()</c>.
/// </remarks>
public class SecretSweepJobOptions
{
    /// <summary>
    ///     Configuration section.
    /// </summary>
    public const string SectionName = "Bot:SecretSweep";

    /// <summary>
    ///     Cron expression (UTC) of the recurring <c>Verify</c> sweep over all tenants. Default daily at
    ///     03:00 UTC. Empty disables the recurring verify (the recurring job is removed).
    /// </summary>
    public string? VerifyCron { get; set; } = "0 3 * * *";

    /// <summary>
    ///     When <c>true</c> (default), every sweep that writes (<c>Encrypt</c>, <c>Reprotect</c>,
    ///     <c>CleanupUnreadable</c>) first takes a fresh mongodump of the tenant (decision 10). If that dump
    ///     cannot be taken, the sweep of that tenant is skipped with the reason in its report - it fails
    ///     safe. Set to <c>false</c> only deliberately (e.g. a local environment without the MongoDB
    ///     database tools); the sweep then runs without a rollback dump.
    /// </summary>
    public bool RequirePreSweepBackup { get; set; } = true;

    /// <summary>
    ///     Directory of the pre-sweep dumps. They hold the secrets as they were before the sweep (possibly
    ///     clear text) and are therefore secret material: kept in their own directory (owner-only
    ///     permissions on Unix), never offered as a job download, deleted after
    ///     <see cref="BackupRetentionDays" />. Must not lie inside the tus or dump directories. Use a
    ///     persistent volume in Kubernetes. Default: <c>&lt;temp&gt;/octo-bot/secret-backups</c>.
    /// </summary>
    public string BackupStoragePath { get; set; } = Path.Combine(Path.GetTempPath(), "octo-bot", "secret-backups");

    /// <summary>
    ///     Days a pre-sweep dump is kept before the hourly cleanup deletes it. Default 7 (decision 10).
    /// </summary>
    public int BackupRetentionDays { get; set; } = 7;

    /// <summary>
    ///     Entities read per repository call by the sweep. Default 500.
    /// </summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    ///     When <c>true</c> (default), a repository restore runs <c>Verify</c>, <c>Encrypt</c> and <c>Verify</c>
    ///     on the restored tenant (concept §6, decisions 2026-10-06 item 2) and reports the secrets that have
    ///     to be re-entered (unknown key id - kept encrypted, never deleted after a restore).
    /// </summary>
    public bool RunAfterRestore { get; set; } = true;

    /// <summary>
    ///     Start of strict mode for this environment (UTC, e.g. <c>2026-11-15T00:00:00Z</c>); <c>null</c>
    ///     (default) = not in strict mode. Decision 10: 14 days after the sweep reported zero plaintext.
    ///     Strict mode means legacy values (clear text and <c>enc:v1</c>) are no longer acceptable. From this
    ///     moment every sweep that still finds legacy values logs an error and reports the tenant in the gauge
    ///     <c>octo.secrets.strict_mode.violations{tenant}</c> - the alert condition "plaintext &gt; 0 after
    ///     strict mode" (concept §5.3). The engine does not refuse legacy reads yet (follow-up).
    /// </summary>
    public DateTimeOffset? StrictModeSince { get; set; }
}
