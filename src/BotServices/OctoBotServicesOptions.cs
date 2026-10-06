using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.BotServices;

/// <summary>
///     Defines job services options
/// </summary>
public class OctoBotServicesOptions
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public OctoBotServicesOptions()
    {
        BrokerHost = "localhost";
        JobDatabaseName = "OctoSystemJobs";
        PrepareJobSchemaIfNecessary = true;
        AuthorityUrl = "https://localhost:5003";
        PublicUrl = "https://localhost:5009";
        PublicRefineryStudioUrl = "https://localhost:4200";
#if DEBUGL || DEBUG
        MinLogLevel = LogLevelDto.Trace;
#else
        MinLogLevel = LogLevelDto.Warn;
#endif
    }

    /// <summary>
    ///     Gets or sets the prefix for the OctoMesh installation instance.
    /// </summary>
    public string? InstancePrefix { get; set; }

    /// <summary>
    ///     Gets or sets the RabbitMq host name
    /// </summary>
    public string BrokerHost { get; set; }

    /// <summary>
    ///     Gets or sets the RabbitMq user
    /// </summary>
    public string? BrokerUser { get; set; }

    /// <summary>
    ///     Gets or sets the RabbitMq password
    /// </summary>
    public string? BrokerPassword { get; set; }

    /// <summary>
    ///     MongoDB job database
    /// </summary>
    public string JobDatabaseName { get; set; }

    /// <summary>
    ///     When true, the collections of mongodb job database are created when they do not exist
    /// </summary>
    public bool PrepareJobSchemaIfNecessary { get; set; }

    /// <summary>
    ///     (public) base address of identity services
    /// </summary>
    public string AuthorityUrl { get; set; }

    /// <summary>
    ///     (public) base address of the public URI
    /// </summary>
    public string PublicUrl { get; set; }

    /// <summary>
    ///     (public) base address of the Data Refinery Studio — used as the Hangfire dashboard's
    ///     "Back to site" link (the legacy admin-panel host it pointed at was retired in Phase 4).
    /// </summary>
    public string PublicRefineryStudioUrl { get; set; }
    
    /// <summary>
    /// Gets or sets the minimal log level to be logged
    /// </summary>
    public LogLevelDto MinLogLevel { get; set; }

    /// <summary>
    /// Gets or sets the storage path for tus resumable uploads.
    /// </summary>
    public string TusStoragePath { get; set; } = Path.Combine(Path.GetTempPath(), "octo-bot", "tus-uploads");

    /// <summary>
    /// Gets or sets the storage path for database dump files.
    /// </summary>
    public string DumpStoragePath { get; set; } = Path.Combine(Path.GetTempPath(), "octo-bot", "dumps");

    /// <summary>
    /// Local scratch directory (owner-only) where mongodump writes before the dump is encrypted into the artifact
    /// store, and where stored artifacts are decrypted for mongorestore (AB#5561). Files live there only while a job
    /// runs. Default <c>&lt;temp&gt;/octo-bot/scratch</c>; the chart mounts a scratch volume at <c>/tmp</c>.
    /// </summary>
    public string ScratchPath { get; set; } = Path.Combine(Path.GetTempPath(), "octo-bot", "scratch");

    /// <summary>
    /// Hours tenant dumps and staged restore uploads are kept in the artifact store before the hourly cleanup
    /// deletes them (AB#5561, default 24; the store's 1-day lifecycle rule is the backstop).
    /// </summary>
    public int ArtifactRetentionHours { get; set; } = 24;

    /// <summary>
    /// Gets or sets the maximum upload size in bytes (default: 10 GB).
    /// </summary>
    public long MaxUploadSizeBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the number of hours to retain temporary files before cleanup (default: 4).
    /// </summary>
    public int FileRetentionHours { get; set; } = 4;

    /// <summary>
    /// Hostname of the CrateDB server backing the tenant stream-data archives. The archive data
    /// export/import jobs (AB#4230) access CrateDB directly through the runtime engine instead of
    /// calling the asset-repo over HTTP.
    /// </summary>
    public string StreamDataHost { get; set; } = "127.0.0.1";

    /// <summary>
    /// User for the CrateDB connection used by the archive data export/import jobs.
    /// </summary>
    public string StreamDataUser { get; set; } = "crate";

    /// <summary>
    /// Password for the CrateDB connection used by the archive data export/import jobs.
    /// </summary>
    public string? StreamDataPassword { get; set; }
}