using Hangfire;
using Meshmakers.Octo.Backend.BotServices.HangfireSetup;
using Meshmakers.Octo.Backend.Jobs;
using Meshmakers.Octo.Backend.Jobs.DumpRegistry;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.BotServices.Services.DumpRegistry;

/// <summary>Wires the dump registry: store, recording, restore check, cleanup and the backfill at start.</summary>
internal static class DumpHashRegistryRegistration
{
    /// <summary>Registers the dump registry on the bot's job database.</summary>
    public static IServiceCollection AddDumpHashRegistry(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<DumpHashRegistryOptions>().Bind(configuration.GetSection(DumpHashRegistryOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(serviceProvider => new MongoDumpHashRegistry(OpenJobDatabase(serviceProvider)));
        services.AddSingleton<IDumpHashRegistry>(serviceProvider => new TenantGenerationDumpHashRegistry(
            serviceProvider.GetRequiredService<MongoDumpHashRegistry>(),
            serviceProvider.GetRequiredService<ICkMethodOperationTenantGeneration>()));
        services.AddSingleton<DumpHashRecorder>();
        services.AddSingleton<ArchiveRegistrationVerifier>();
        services.AddSingleton<RestoreArchiveCheckPolicy>();
        services.AddSingleton<DumpHashGenerationCleanup>();
        services.AddTransient<IDumpHashBackfillJob, DumpHashBackfillJob>();
        services.AddHostedService<DumpHashBackfillStartup>();
        return services;
    }

    private static IMongoDatabase OpenJobDatabase(IServiceProvider serviceProvider)
    {
        var botOptions = serviceProvider.GetRequiredService<IOptions<OctoBotServicesOptions>>().Value;
        var systemOptions = serviceProvider.GetRequiredService<IOptions<OctoSystemConfiguration>>().Value;
        var jobDatabaseUrl = JobDatabaseUrl.Create(botOptions, systemOptions);
        return new MongoClient(jobDatabaseUrl.ToMongoUrl()).GetDatabase(jobDatabaseUrl.DatabaseName);
    }
}

/// <summary>Enqueues the dump registry backfill once when the service starts; the job skips registered dumps.</summary>
internal sealed class DumpHashBackfillStartup(
    IBackgroundJobClient backgroundJobClient,
    ILogger<DumpHashBackfillStartup> logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var jobId = backgroundJobClient.Enqueue<IDumpHashBackfillJob>(job => job.Run(BotCancellationToken.Null));
            logger.LogInformation("Dump registry backfill enqueued as job '{JobId}'", jobId);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Dump registry backfill could not be enqueued; it runs at the next start");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
