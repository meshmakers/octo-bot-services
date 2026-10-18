using Meshmakers.Octo.Services.ArtifactStorage;
using Meshmakers.Octo.Services.ArtifactStorage.Configuration;
using Meshmakers.Octo.Services.ArtifactStorage.Staging;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Services;

/// <summary>
///     One info line at start-up about the artifact store (AB#6969): provider, bucket or container, whether a CrateDB endpoint
///     is set and whether the native archive copy (copy staging) is possible (logged as a warning when it is not, so the default
///     log level shows it). Only names and yes/no answers are logged - never
///     a key, secret, SAS token, connection string or a URI that may carry credentials.
/// </summary>
public static class ArtifactStoreStartupLog
{
    /// <summary>Writes the line.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="options">The bound <c>ArtifactStorage</c> section.</param>
    /// <param name="stagingFactory">The staging location factory; <c>null</c> when none is registered.</param>
    public static void Write(ILogger logger, ArtifactStorageOptions options, IArchiveCopyStagingLocationFactory? stagingFactory)
    {
        var (location, crateEndpoint) = options.Provider switch
        {
            ArtifactStorageProvider.S3 => ($"bucket '{options.S3.Bucket}'", !string.IsNullOrWhiteSpace(options.S3.CrateDbServiceUrl)),
            ArtifactStorageProvider.AzureBlob => ($"container '{options.AzureBlob.Container}'", false),
            _ => ("a local directory", false)
        };

        var staging = stagingFactory is null
            ? "no (no staging location factory is registered)"
            : stagingFactory.IsSupported
                ? "yes"
                : $"no ({stagingFactory.UnsupportedReason ?? "not supported by this store"})";

        var stagingPossible = stagingFactory is { IsSupported: true };
        const string template =
            "Artifact store: provider {Provider}, {Location}, instance prefix '{InstancePrefix}', CrateDB endpoint set: {CrateDbEndpoint}, " +
            "copy staging possible: {Staging}";

        // Warning when the native copy is not possible, so that an operator sees it with the default log level.
        logger.Log(stagingPossible ? LogLevel.Information : LogLevel.Warning, template,
            options.Provider, location, options.InstancePrefix, crateEndpoint ? "yes" : "no", staging);
    }
}

/// <summary>
///     Writes the <see cref="ArtifactStoreStartupLog" /> line when the host starts (a hosted service, so it appears in the same
///     log stream as the other start-up lines of the services).
/// </summary>
public sealed class ArtifactStoreStartupLogService(
    ILogger<ArtifactStoreStartupLogService> logger,
    Microsoft.Extensions.Options.IOptions<ArtifactStorageOptions> options,
    IServiceProvider services) : Microsoft.Extensions.Hosting.IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ArtifactStoreStartupLog.Write(logger, options.Value,
            (IArchiveCopyStagingLocationFactory?)services.GetService(typeof(IArchiveCopyStagingLocationFactory)));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
