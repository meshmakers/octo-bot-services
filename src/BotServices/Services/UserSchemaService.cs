using BotServices.Resources;
using IdentityModel;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands.Payloads;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.BotServices.Services;

internal class UserSchemaService : IUserSchemaService
{
    private readonly ICommandClient<CreateIdentityDataCommandRequest> _commandClient;

    private readonly OctoBotServicesOptions _octoBotServicesOptions;
    private readonly ISystemContext _systemContext;

    public UserSchemaService(ISystemContext systemContext,
        ICommandClient<CreateIdentityDataCommandRequest> commandClient,
        IOptions<OctoBotServicesOptions> octoBotServicesOptions)
    {
        _commandClient = commandClient;

        _systemContext = systemContext;
        _octoBotServicesOptions = octoBotServicesOptions.Value;
    }

    public async Task SetupAsync()
    {
        using var session = await _systemContext.GetSystemSessionAsync();
        session.StartTransaction();

        var botServiceConfiguration =
            await _systemContext.GetConfigurationAsync(session, BotServiceConstants.BotServiceSchemaVersionKey,
                new DefaultConfigurationVersion { Version = -1 });
        if (botServiceConfiguration == null || botServiceConfiguration.Version < BotServiceConstants.BotServiceSchemaVersionValue)
        {
            CreateIdentityDataCommandRequest createIdentityDataCommandRequest = new(null);
            CreateApiScopes(createIdentityDataCommandRequest);
            CreateApiResources(createIdentityDataCommandRequest);
            CreateClients(createIdentityDataCommandRequest);

            await _commandClient.GetResponse<GenericCommandResponse>(createIdentityDataCommandRequest);

            await _systemContext.SetConfigurationAsync(session, BotServiceConstants.BotServiceSchemaVersionKey,
                new DefaultConfigurationVersion { Version = BotServiceConstants.BotServiceSchemaVersionValue });
        }

        await session.CommitTransactionAsync();
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
            new (CommonConstants.BotApi, CommonConstants.BotApiDisplayName)
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

                PostLogoutRedirectUris = [ _octoBotServicesOptions.PublicAdminPanelUrl.EnsureEndsWith("/") ],
                AllowedCorsOrigins = [ _octoBotServicesOptions.PublicAdminPanelUrl.TrimEnd('/') ],
                AllowOfflineAccess = true,
                AllowedScopes =
                [
                    CommonConstants.Scopes.OpenId,
                    CommonConstants.Scopes.Profile,
                    CommonConstants.Scopes.Email,
                    JwtClaimTypes.Role
                ]
            },
            new (CommonConstants.OctoBotServicesSwaggerClientId, 
                BotTexts.Backend_BotServices_UserSchema_Swagger_DisplayName, 
                _octoBotServicesOptions.PublicUrl)
            {
                AllowedGrantTypes = [OidcConstants.GrantTypes.AuthorizationCode],
            
                RedirectUris =
                [
                    _octoBotServicesOptions.PublicUrl.EnsureEndsWith("/swagger/oauth2-redirect.html")
                ],
            
                PostLogoutRedirectUris = [ _octoBotServicesOptions.PublicUrl.EnsureEndsWith("/") ],
                AllowedCorsOrigins = [ _octoBotServicesOptions.PublicUrl.TrimEnd('/') ],
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
