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

internal class DefaultConfigurationCreatorService : DefaultConfigurationCreatorServiceBase
{
    private readonly ILogger<DefaultConfigurationCreatorService> _logger;
    private readonly ICommandClient<CreateIdentityDataCommandRequest> _commandClient;

    private readonly OctoBotServicesOptions _octoBotServicesOptions;
    private readonly ISystemContext _systemContext;
    private readonly IJobCreatorService _jobCreatorService;

    public DefaultConfigurationCreatorService(ILoggerFactory loggerFactory, ISystemContext systemContext, IJobCreatorService jobCreatorService,
        ICommandClient<CreateIdentityDataCommandRequest> commandClient,
        IOptions<OctoBotServicesOptions> octoBotServicesOptions)
        : base(loggerFactory.CreateLogger<DefaultConfigurationCreatorServiceBase>())
    {
        _logger = loggerFactory.CreateLogger<DefaultConfigurationCreatorService>();
        _commandClient = commandClient;

        _systemContext = systemContext;
        _jobCreatorService = jobCreatorService;
        _octoBotServicesOptions = octoBotServicesOptions.Value;
    }

    protected override async Task SetupTenantAsync(string tenantId)
    {
        // Do nothing if the system tenant is not existing.
        // Identity Service is creating the system tenant currently.
        if (!await _systemContext.IsSystemTenantExistingAsync())
        {
            return;
        }
        
        // That means that the system tenant database is existing but (currently) not valid.
        // We wait for a PosTenantCreated event to create the default configuration.
        if (!await _systemContext.IsCkModelExistingAsync(SystemCkIds.ModelId))
        {
            return;
        }
        
        _logger.LogInformation("Setting up default configuration for tenant '{TenantId}'", tenantId);
        
        await ImportCkModelAsync(tenantId);

        // Identity configuration is next
        if (tenantId != _systemContext.TenantId)
        {
            // Currently we only support the system tenant.
            return;
        }
        
        using var session = await _systemContext.GetSystemSessionAsync();
        session.StartTransaction();

        var botServiceConfiguration =
            await _systemContext.GetConfigurationAsync(session, BotServiceConstants.BotServiceSchemaVersionKey,
                new DefaultConfigurationVersion { Version = -1 });
        if (botServiceConfiguration == null || botServiceConfiguration.Version < BotServiceConstants.BotServiceSchemaVersionValue)
        {
            _logger.LogInformation("Creating identity data for tenant '{TenantId}'", tenantId);

            CreateIdentityDataCommandRequest createIdentityDataCommandRequest = new(_systemContext.TenantId);
            CreateApiScopes(createIdentityDataCommandRequest);
            CreateApiResources(createIdentityDataCommandRequest);
            CreateClients(createIdentityDataCommandRequest);

            await _commandClient.GetResponse<GenericCommandResponse>(createIdentityDataCommandRequest);

            await _systemContext.SetConfigurationAsync(session, BotServiceConstants.BotServiceSchemaVersionKey,
                new DefaultConfigurationVersion { Version = BotServiceConstants.BotServiceSchemaVersionValue });
        }

        await session.CommitTransactionAsync();
        
        // Create jobs
        _jobCreatorService.DeleteJobs(tenantId);
        _jobCreatorService.CreateJobs(tenantId);
        
        _logger.LogInformation("Setup default configuration for tenant '{TenantId}' completed", tenantId);
    }

    private async Task ImportCkModelAsync(string tenantId)
    {
        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
        
        if (!await tenantContext.IsCkModelExistingAsync(SystemBotCkIds.ModelId))
        {
            OperationResult operationResult = new();
            await tenantContext.ImportCkModelAsync(SystemBotCkIds.ModelId, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw InitializationException.ImportCkModelFailed(tenantContext.TenantId, operationResult.GetMessages());
            }
        }

        if (!await tenantContext.IsCkModelExistingAsync(SystemNotificationCkIds.ModelId))
        {
            OperationResult operationResult = new();
            await tenantContext.ImportCkModelAsync(SystemNotificationCkIds.ModelId, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw InitializationException.ImportCkModelFailed(tenantContext.TenantId, operationResult.GetMessages());
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
                _octoBotServicesOptions.PublicAdminPanelUrl)
            {
                AllowedGrantTypes = [OidcConstants.GrantTypes.Implicit],

                RequireConsent = false,

                RedirectUris =
                [
                    _octoBotServicesOptions.PublicUrl.EnsureEndsWith("/") + "signin-oidc"
                ],

                PostLogoutRedirectUris = [_octoBotServicesOptions.PublicAdminPanelUrl.EnsureEndsWith("/")],
                AllowedCorsOrigins = [_octoBotServicesOptions.PublicAdminPanelUrl.TrimEnd('/')],
                AllowOfflineAccess = true,
                AllowedScopes =
                [
                    CommonConstants.Scopes.OpenId,
                    CommonConstants.Scopes.Profile,
                    CommonConstants.Scopes.Email,
                    JwtClaimTypes.Role
                ]
            },
            new(CommonConstants.OctoBotServicesSwaggerClientId,
                BotTexts.Backend_BotServices_UserSchema_Swagger_DisplayName,
                _octoBotServicesOptions.PublicUrl)
            {
                AllowedGrantTypes = [OidcConstants.GrantTypes.AuthorizationCode],

                RedirectUris =
                [
                    _octoBotServicesOptions.PublicUrl.EnsureEndsWith("/swagger/oauth2-redirect.html")
                ],

                PostLogoutRedirectUris = [_octoBotServicesOptions.PublicUrl.EnsureEndsWith("/")],
                AllowedCorsOrigins = [_octoBotServicesOptions.PublicUrl.TrimEnd('/')],
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