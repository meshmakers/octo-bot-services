using Meshmakers.Octo.Services.Infrastructure.Cors;

namespace Meshmakers.Octo.Backend.BotServices.Configuration;

/// <summary>
///     AB#7270: bot-services answers browsers with the shared known-origins CORS policy of
///     <c>AddOctoServiceInfrastructure()</c> (allowed origins = registered client origins of the addressed tenant,
///     no credentials) like every other OctoMesh service. This class only adds what the tus upload protocol needs
///     on top: the response headers the browser client must read, and a preflight cache lifetime.
/// </summary>
internal static class BotCorsConfiguration
{
    /// <summary>
    ///     Response headers of the tus protocol that the browser upload client reads.
    /// </summary>
    public static readonly IReadOnlyList<string> TusExposedHeaders =
        ["Upload-Offset", "Upload-Length", "Tus-Resumable", "Location"];

    /// <summary>
    ///     Lifetime of a preflight result in the browser cache.
    /// </summary>
    public static readonly TimeSpan PreflightMaxAge = TimeSpan.FromHours(1);

    /// <summary>
    ///     Applies the bot-service additions to the shared CORS policy.
    /// </summary>
    public static void Configure(OctoCorsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var header in TusExposedHeaders)
        {
            if (!options.ExposedHeaders.Contains(header))
            {
                options.ExposedHeaders.Add(header);
            }
        }

        options.PreflightMaxAge = PreflightMaxAge;
    }
}
