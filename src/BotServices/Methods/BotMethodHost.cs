using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.Jobs.Jobs.Operations;
using Meshmakers.Octo.ConstructionKit.Models.System.Bot.Generated.System.Bot.v3;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.BotServices.Methods;

/// <summary>
///     The one place that hosts CK methods in the bot service (AB#8445: dump; AB#8447: restore, archive data export and
///     import). A new method adds a handler class and one line to <see cref="AddBotCkMethodHost" />; host, caller token
///     validation and the operation runtime are set up here.
/// </summary>
internal static class BotMethodHost
{
    /// <summary>Name of this service in the method handler registry.</summary>
    public const string ServiceName = "BotService";

    /// <summary>
    ///     Registers the method host: the handler of <c>System.Bot/TenantRepository.Dump-1</c>, the caller token validation
    ///     against the authority of this service's HTTP API, the operation runtime with the MongoDB operation store (system
    ///     database, collection <c>MethodOperations</c>, shared with the asset repository that reads it through
    ///     <c>runtime.operations</c>) and the gate the dump job asks before it starts. The event hub part
    ///     (<c>AddCkMethodHandlerDiscovery</c>) is added in <c>Program.cs</c>.
    /// </summary>
    public static IServiceCollection AddBotCkMethodHost(this IServiceCollection services)
    {
        services.AddCkMethodHandlers(ServiceName, m => m
            .Handle<TenantRepositoryDumpHandler>(SystemBotCkIds.TenantRepositoryDumpMethodId)
            .Handle<TenantRepositoryRestoreHandler>(SystemBotCkIds.TenantRepositoryRestoreMethodId)
            .Handle<TenantRepositoryExportArchiveDataHandler>(SystemBotCkIds.TenantRepositoryExportArchiveDataMethodId)
            .Handle<TenantRepositoryImportArchiveDataHandler>(SystemBotCkIds.TenantRepositoryImportArchiveDataMethodId));

        // The bus is internal but not authenticated per message: the host validates the forwarded access token with the
        // authority of this service's HTTP API (issuer, signature, expiry, tenant, scope), then the declared roles.
        services.AddCkMethodCallerTokenValidation();
        services.AddOptions<CkMethodTokenOptions>()
            .Configure<IOptions<OctoBotServicesOptions>, IWebHostEnvironment>((token, bot, environment) =>
            {
                if (string.IsNullOrWhiteSpace(token.Authority) && string.IsNullOrWhiteSpace(token.MetadataAddress))
                {
                    var authority = bot.Value.AuthorityUrl.EnsureEndsWith("/");
                    token.Authority = authority;
                    token.ValidIssuer ??= authority;
                }

                if (environment.IsDevelopment())
                {
                    token.RequireHttpsMetadata = false;
                }
            });
        // Development: the authority is the local self-signed https endpoint (same rule as the identity service host).
        services.AddHttpClient(CkMethodTokenOptions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var handler = new HttpClientHandler();
                if (sp.GetRequiredService<IWebHostEnvironment>().IsDevelopment())
                {
                    handler.ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                }

                return handler;
            });

        // Async methods (F3.6, AB#8443): durable operation records in the system database, runner, heartbeat, sweep.
        services.AddCkMethodMongoOperationStore();
        services.AddCkMethodOperations();
        services.TryAddSingleton<IDumpOperationGate, DumpOperationGate>();
        // AB#8881: the tenant setup removes records an earlier tenant with the same id left behind (reads ignore them)
        services.TryAddSingleton<OperationRecordGenerationCleanup>();

        services.AddOptions<RepositoryDumpMethodOptions>()
            .BindConfiguration(RepositoryDumpMethodOptions.SectionName)
            .Validate(o => o.PollInterval >= TimeSpan.FromMilliseconds(100),
                $"{RepositoryDumpMethodOptions.SectionName}:PollInterval must be at least 100 ms")
            .ValidateOnStart();
        services.AddOptions<BotJobMethodOptions>()
            .BindConfiguration(BotJobMethodOptions.SectionName)
            .Validate(o => o.PollInterval >= TimeSpan.FromMilliseconds(100),
                $"{BotJobMethodOptions.SectionName}:PollInterval must be at least 100 ms")
            .ValidateOnStart();
        return services;
    }
}
