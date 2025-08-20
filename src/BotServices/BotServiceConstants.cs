namespace Meshmakers.Octo.Backend.BotServices;

internal static class BotServiceConstants
{
    /// <summary>
    ///     Name of key of database schema
    /// </summary>
    public const string BotServiceSchemaVersionKey = "BotServices";

    /// <summary>
    ///     Version of database schema for job service specific data
    /// </summary>
    public const int BotServiceSchemaVersionValue = 3;

    /// <summary>
    /// Name of the key identity data
    /// </summary>
    public const string BotServiceIdentityDataVersionKey = "BotServicesIdentityData";

    /// <summary>
    /// Expected value of the identity data version
    /// </summary>
    public const int BotServiceIdentityDataVersionValue = 1;

    /// <summary>
    ///     The name of the cookie of cookie-based auth
    /// </summary>
    public const string CookieName = "Octo-BotServices";

    /// <summary>
    ///     Policy for authenticated users authorization
    /// </summary>
    public const string AuthenticatedUserPolicy = "AuthenticatedUserPolicy";

    /// <summary>
    ///     Policy for job api read only authorization
    /// </summary>
    public const string JobApiReadOnlyPolicy = "JobApiReadOnlyPolicy";
    
    /// <summary>
    ///     Policy for job api read write authorization
    /// </summary>
    public const string JobApiReadWritePolicy = "JobApiReadWritePolicy";

    /// <summary>
    ///     Timespan a cookie is expiring
    /// </summary>
    public static readonly TimeSpan CookieExpireTimeSpan = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Default prefix for instance name
    /// </summary>
    public const string  DefaultInstancePrefix = "default";
}