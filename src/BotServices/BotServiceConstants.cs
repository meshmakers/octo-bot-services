using System;

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
    public const int BotServiceSchemaVersionValue = 1;

    /// <summary>
    ///     The name of the cookie of cookie-based auth
    /// </summary>
    public const string CookieName = "Octo-BotServices";

    /// <summary>
    ///     Policy for authenticated users authorization
    /// </summary>
    public const string AuthenticatedUserPolicy = "AuthenticatedUserPolicy";

    public const string JobApiReadOnlyPolicy = "JobApiReadOnlyPolicy";
    public const string JobApiReadWritePolicy = "JobApiReadWritePolicy";

    /// <summary>
    ///     Timespan a cookie is expiring
    /// </summary>
    public static readonly TimeSpan CookieExpireTimeSpan = TimeSpan.FromMinutes(60);
}
