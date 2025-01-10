using BotServices.Resources;
using IdentityModel;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands.Payloads;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v1;
using Microsoft.Extensions.Options;
using SystemBotCkModel.Generated.System.Bot.v1;

namespace Meshmakers.Octo.Backend.BotServices.Services;

internal class DefaultConfigurationCreatorService(
    ILoggerFactory loggerFactory,
    IDiagnosticsService diagnosticsService,
    ISystemContext systemContext,
    IJobCreatorService jobCreatorService,
    ICommandClient<CreateIdentityDataCommandRequest> commandClient,
    IOptions<OctoBotServicesOptions> octoBotServicesOptions)
    : DefaultConfigurationCreatorServiceBase(loggerFactory.CreateLogger<DefaultConfigurationCreatorServiceBase>())
{
    private readonly ILogger<DefaultConfigurationCreatorService> _logger = loggerFactory.CreateLogger<DefaultConfigurationCreatorService>();

    public override async Task InitializeAsync()
    {
        // Reconfigure the log level based on the configuration
        await diagnosticsService.ReconfigureLogLevelAsync(octoBotServicesOptions.Value.MinLogLevel);

        await base.InitializeAsync();
    }

    protected override async Task SetupTenantAsync(string tenantId)
    {
        // Do nothing if the system tenant is not existing.
        // Identity Service is creating the system tenant currently.
        // We wait for a PosTenantCreated event to create the default configuration.
        if (!await systemContext.IsSystemTenantExistingAsync())
        {
            return;
        }

        _logger.LogInformation("Setting up default configuration for tenant '{TenantId}'", tenantId);

        await ImportCkModelAsync(tenantId);

        // Identity configuration is next
        if (tenantId != systemContext.TenantId)
        {
            // Currently we only support the system tenant.
            return;
        }
        
        _logger.LogInformation("Setting up default identity data for tenant '{TenantId}'", tenantId);

        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var botServiceConfiguration =
            await systemContext.GetConfigurationAsync(session, BotServiceConstants.BotServiceSchemaVersionKey,
                new DefaultConfigurationVersion { Version = -1 });
        if (botServiceConfiguration == null ||
            botServiceConfiguration.Version < BotServiceConstants.BotServiceSchemaVersionValue)
        {
            _logger.LogInformation("Creating identity data for tenant '{TenantId}'", tenantId);

            CreateIdentityDataCommandRequest createIdentityDataCommandRequest = new(systemContext.TenantId);
            CreateApiScopes(createIdentityDataCommandRequest);
            CreateApiResources(createIdentityDataCommandRequest);
            CreateClients(createIdentityDataCommandRequest);

            _logger.LogInformation("Creating identity data for tenant '{TenantId}'", tenantId);
            var r = await commandClient.GetResponseWithRetry<EnumCommandResponse<CreateIdentityDataResult>>(
                createIdentityDataCommandRequest);
            _logger.LogInformation("Create identity data response: {Response}", r.Response);
            if (r.Response == CreateIdentityDataResult.Success)
            {
                await systemContext.SetConfigurationAsync(session, BotServiceConstants.BotServiceSchemaVersionKey,
                    new DefaultConfigurationVersion { Version = BotServiceConstants.BotServiceSchemaVersionValue });
            }
            else if (r.Response != CreateIdentityDataResult.FailedTenantHasNoIdentityCk)
            {
                _logger.LogInformation("The tenant '{TenantId}' has no identity CK, skipped to create identity data",
                    tenantId);
            }
            else
            {
                _logger.LogError("The tenant '{TenantId}' has no identity CK, skipped to create identity data",
                    tenantId);
            }
        }

        await session.CommitTransactionAsync();

        // Create jobs
        jobCreatorService.DeleteJobs(tenantId);
        jobCreatorService.CreateJobs(tenantId);

        _logger.LogInformation("Setup default configuration for tenant '{TenantId}' completed", tenantId);
    }

    private async Task ImportCkModelAsync(string tenantId)
    {
        var tenantContext = await systemContext.FindTenantContextAsync(tenantId);

        if (!await tenantContext.IsCkModelExistingAsync(SystemBotCkIds.ModelId))
        {
            OperationResult operationResult = new();
            await tenantContext.ImportCkModelAsync(SystemBotCkIds.ModelId, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw InitializationException.ImportCkModelFailed(tenantContext.TenantId,
                    operationResult.GetMessages());
            }
        }

        if (!await tenantContext.IsCkModelExistingAsync(SystemNotificationCkIds.ModelId))
        {
            OperationResult operationResult = new();
            await tenantContext.ImportCkModelAsync(SystemNotificationCkIds.ModelId, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw InitializationException.ImportCkModelFailed(tenantContext.TenantId,
                    operationResult.GetMessages());
            }
        }
    }

    private void CreateApiScopes(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    {
        createIdentityDataCommandRequest.ApiScopes = new List<DistApiScopeDto>
        {
            new(CommonConstants.BotApiFullAccess,
                CommonConstants.BotApiFullAccessDisplayName),
            new(CommonConstants.BotApiReadOnly,
                CommonConstants.BotApiReadOnlyDisplayName)
        };
    }

    private void CreateApiResources(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    {
        createIdentityDataCommandRequest.ApiResources = new List<DistApiResourcesDto>
        {
            new(CommonConstants.BotApi, CommonConstants.BotApiDisplayName)
            {
                Description = CommonConstants.BotApiDescription,
                IsEnabled = true,
                Scopes = new List<string>
                {
                    CommonConstants.BotApiFullAccess,
                    CommonConstants.BotApiReadOnly
                }
            }
        };
    }

    private void CreateClients(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    {
        createIdentityDataCommandRequest.Clients = new List<DistClientDto>
        {
            new(CommonConstants.BotServicesClientId,
                BotTexts.Backend_BotServices_UserSchema_BotServices_DisplayName,
                octoBotServicesOptions.Value.PublicUrl)
            {
                AllowedGrantTypes = [OidcConstants.GrantTypes.Implicit],

                RequireConsent = false,

                RedirectUris =
                [
                    octoBotServicesOptions.Value.PublicUrl.EnsureEndsWith("/") + "signin-oidc"
                ],

                PostLogoutRedirectUris = [octoBotServicesOptions.Value.PublicUrl.EnsureEndsWith("/")],
                AllowedCorsOrigins = [octoBotServicesOptions.Value.PublicUrl.TrimEnd('/')],
                AllowOfflineAccess = true,
                AllowedScopes =
                [
                    CommonConstants.Scopes.OpenId,
                    CommonConstants.Scopes.Profile,
                    CommonConstants.Scopes.Email,
                    JwtClaimTypes.Role
                ]
            },
            new(CommonConstants.BotServicesSwaggerClientId,
                BotTexts.Backend_BotServices_UserSchema_Swagger_DisplayName,
                octoBotServicesOptions.Value.PublicUrl)
            {
                AllowedGrantTypes = [OidcConstants.GrantTypes.AuthorizationCode],

                RedirectUris =
                [
                    octoBotServicesOptions.Value.PublicUrl.EnsureEndsWith("/swagger/oauth2-redirect.html")
                ],

                PostLogoutRedirectUris = [octoBotServicesOptions.Value.PublicUrl.EnsureEndsWith("/")],
                AllowedCorsOrigins = [octoBotServicesOptions.Value.PublicUrl.TrimEnd('/')],
                AllowedScopes =
                [
                    CommonConstants.Scopes.OpenId,
                    CommonConstants.Scopes.Profile,
                    CommonConstants.Scopes.Email,
                    JwtClaimTypes.Role,
                    CommonConstants.BotApiFullAccess,
                    CommonConstants.BotApiReadOnly
                ]
            }
        };
    }
}