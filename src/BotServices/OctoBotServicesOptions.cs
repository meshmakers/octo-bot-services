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
        JobDatabaseName = "OctoSystemJobs";
        PrepareJobSchemaIfNecessary = true;
        AuthorityUrl = "https://localhost:5003";
        PublicUrl = "https://localhost:5009";
        PublicAdminPanelUrl = "https://localhost:5005";
        RedisCacheHost = "localhost";
    }

    /// <summary>
    ///     Gets or sets the redis cache host name
    /// </summary>
    public string RedisCacheHost { get; set; }

    /// <summary>
    ///     Gets or sets the redis cache password
    /// </summary>
    public string RedisCachePassword { get; set; }

    /// <summary>
    ///     URL of arango db
    /// </summary>
    public string JobDatabaseName { get; set; }

    /// <summary>
    ///     When true, the collections of arango db job database are created when they do not exist
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
}
