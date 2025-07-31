using BotServices.Resources;
using IdentityModel;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands.Payloads;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v1;
using Microsoft.Extensions.Options;
using SystemBotCkModel.Generated.System.Bot.v1;

namespace Meshmakers.Octo.Backend.BotServices.Services;

internal class DefaultConfigurationCreatorService(
    ILogger<DefaultConfigurationCreatorService> logger,
    IDiagnosticsService diagnosticsService,
    ISystemContext systemContext,
    IJobCreatorService jobCreatorService,
    ICommandClient<CreateIdentityDataCommandRequest> createIdentityDataCommandClient,
    IOptions<OctoBotServicesOptions> octoBotServicesOptions)
    : DefaultConfigurationCreatorServiceStandardized(logger, systemContext, createIdentityDataCommandClient,
        BotServiceConstants.BotServiceIdentityDataVersionKey, BotServiceConstants.BotServiceIdentityDataVersionValue,
        null,// we don't need migrations here
        BotServiceConstants.BotServiceSchemaVersionKey, true, BotServiceConstants.BotServiceSchemaVersionValue)
{
    public override async Task InitializeAsync()
    {
        // Reconfigure the log level based on the configuration
        await diagnosticsService.ReconfigureLogLevelAsync(octoBotServicesOptions.Value.MinLogLevel);

        await base.InitializeAsync();
    }

    protected override Task StartTenantAsync(string tenantId)
    {
        // Create jobs
        jobCreatorService.DeleteJobs(octoBotServicesOptions.Value.InstancePrefix, tenantId);
        jobCreatorService.CreateJobs(octoBotServicesOptions.Value.InstancePrefix, tenantId);

        return base.StartTenantAsync(tenantId);
    }

    protected override Task StopTenantAsync(string tenantId)
    {
        // Delete jobs
        jobCreatorService.DeleteJobs(octoBotServicesOptions.Value.InstancePrefix, tenantId);

        return base.StopTenantAsync(tenantId);
    }

    protected override async Task ImportCkModelAsync(IOctoAdminSession session, ITenantContext tenantContext)
    {
        OperationResult operationResult = new();
        await tenantContext.ImportCkModelAsync(SystemBotCkIds.ModelId, operationResult);
        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            throw InitializationException.ImportCkModelFailed(tenantContext.TenantId,
                operationResult.GetMessages());
        }

        await tenantContext.ImportCkModelAsync(SystemNotificationCkIds.ModelId, operationResult);
        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            throw InitializationException.ImportCkModelFailed(tenantContext.TenantId,
                operationResult.GetMessages());
        }
    }


    protected override void CreateApiScopes(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    {
        createIdentityDataCommandRequest.ApiScopes = new List<DistApiScopeDto>
        {
            new(CommonConstants.BotApiFullAccess,
                CommonConstants.BotApiFullAccessDisplayName),
            new(CommonConstants.BotApiReadOnly,
                CommonConstants.BotApiReadOnlyDisplayName)
        };
    }

    protected override void CreateApiResources(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
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

    protected override void CreateClients(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
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