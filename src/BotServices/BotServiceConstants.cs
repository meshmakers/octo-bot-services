namespace Meshmakers.Octo.Backend.BotServices;

internal static class BotServiceConstants
{
    /// <summary>
    ///     Name of key of database schema
    /// </summary>
    public const string BotServiceSchemaVersionKey = "BotServices";

    /// <summary>
    /// Name of the key identity data
    /// </summary>
    public const string BotServiceIdentityDataVersionKey = "BotServicesIdentityData";

    /// <summary>
    /// Expected value of the identity data version
    /// </summary>
    public const int BotServiceIdentityDataVersionValue = 3;

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
    ///     Policy of the secrets admin operations that change data (AB#5544, contract §6): trigger a secret
    ///     sweep, delete a pre-sweep dump early. Full-access scope plus the tenant role
    ///     <c>SecretManagement</c>.
    /// </summary>
    public const string SecretManagementPolicy = "SecretManagementPolicy";

    /// <summary>
    ///     Policy of the secrets admin read operations (AB#5544, contract §6): last sweep report, sweep run
    ///     list. Read scope plus the tenant role <c>AdminPanelManagement</c>.
    /// </summary>
    public const string SecretAdministrationReadPolicy = "SecretAdministrationReadPolicy";

    /// <summary>
    ///     Timespan a cookie is expiring
    /// </summary>
    public static readonly TimeSpan CookieExpireTimeSpan = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Recurring job id of the daily secret Verify sweep over all tenants (AB#5539).
    /// </summary>
    public const string SecretSweepVerifyRecurringJobId = "secret-sweep-verify";

    /// <summary>
    /// Recurring job id of the never-scheduled secret Encrypt sweep over all tenants, triggered on demand
    /// from the dashboard (AB#5539).
    /// </summary>
    public const string SecretSweepEncryptRecurringJobId = "secret-sweep-encrypt";

    /// <summary>
    /// Default prefix for instance name
    /// </summary>
    public const string  DefaultInstancePrefix = "default";
}