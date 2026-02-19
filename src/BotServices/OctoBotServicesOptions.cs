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
        PublicAdminPanelUrl = "https://localhost:5005";
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
    ///     (public) base address of the dashboard
    /// </summary>
    public string PublicAdminPanelUrl { get; set; }
    
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
    /// Gets or sets the maximum upload size in bytes (default: 10 GB).
    /// </summary>
    public long MaxUploadSizeBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the number of hours to retain temporary files before cleanup (default: 4).
    /// </summary>
    public int FileRetentionHours { get; set; } = 4;
}