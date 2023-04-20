using System.Collections.Generic;
using System.Threading.Tasks;
using BotServices.Resources;
using Duende.IdentityServer.Models;
using IdentityModel;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.SystemEntities;
using Meshmakers.Octo.SystematizedData.Persistence.SystemStores;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.BotServices.Services;

internal class UserSchemaService : IUserSchemaService
{
    private readonly IOctoClientStore _clientStore;
    private readonly OctoBotServicesOptions _octoBotServicesOptions;
    private readonly IOctoResourceStore _resourceStore;
    private readonly ISystemContext _systemContext;

    public UserSchemaService(ISystemContext systemContext, IOctoResourceStore resourceStore,
        IOctoClientStore clientStore,
        IOptions<OctoBotServicesOptions> octoBotServicesOptions)
    {
        _systemContext = systemContext;
        _resourceStore = resourceStore;
        _clientStore = clientStore;
        _octoBotServicesOptions = octoBotServicesOptions.Value;
    }

    public async Task SetupAsync()
    {
        using var session = await _systemContext.StartSystemSessionAsync();
        session.StartTransaction();

        var version =
            await _systemContext.GetConfigurationAsync(session, BotServiceConstants.BotServiceSchemaVersionKey, 0);
        if (version < BotServiceConstants.BotServiceSchemaVersionValue)
        {
            await CreateApiScopes();
            await CreateApiResources();
            await CreateClients();

            await _systemContext.SetConfigurationAsync(session, BotServiceConstants.BotServiceSchemaVersionKey,
                BotServiceConstants.BotServiceSchemaVersionValue);
        }

        await session.CommitTransactionAsync();
    }

    private async Task CreateApiScopes()
    {
        await _resourceStore.TryCreateApiScopeAsync(new ApiScope(CommonConstants.BotApiFullAccess,
            CommonConstants.BotApiFullAccessDisplayName));
        await _resourceStore.TryCreateApiScopeAsync(new ApiScope(CommonConstants.BotApiReadOnly,
            CommonConstants.BotApiReadOnlyDisplayName));
    }

    private async Task CreateApiResources()
    {
        await _resourceStore.GetOrCreateApiResourceAsync(new ApiResource
        {
            Name = CommonConstants.BotApi,
            DisplayName = CommonConstants.BotApiDisplayName,
            Description = CommonConstants.BotApiDescription,
            Enabled = true,
            Scopes = new List<string>
            {
                CommonConstants.BotApiFullAccess,
                CommonConstants.BotApiReadOnly
            }
        });
    }

    private async Task CreateClients()
    {
        var octoBotServices = await _clientStore.FindClientByIdAsync(CommonConstants.BotServicesClientId);
        if (octoBotServices == null)
        {
            var appClient = new OctoClient
            {
                ClientId = CommonConstants.BotServicesClientId,

                ClientName = BotTexts.Backend_BotServices_UserSchema_BotServices_DisplayName,
                ClientUri = _octoBotServicesOptions.PublicUrl,

                AllowedGrantTypes = new[] { OidcConstants.GrantTypes.Implicit },

                RequirePkce = true,
                RequireClientSecret = false,

                AccessTokenType = AccessTokenType.Jwt,
                AllowAccessTokensViaBrowser = true,
                AlwaysIncludeUserClaimsInIdToken = true,

                RedirectUris =
                {
                    _octoBotServicesOptions.PublicUrl.EnsureEndsWith("/") + "signin-oidc"
                },

                PostLogoutRedirectUris = { _octoBotServicesOptions.PublicUrl.EnsureEndsWith("/") },
                AllowedCorsOrigins = { _octoBotServicesOptions.PublicUrl.TrimEnd('/') },
                AllowedScopes =
                {
                    CommonConstants.Scopes.OpenId,
                    CommonConstants.Scopes.Profile,
                    CommonConstants.Scopes.Email,
                    JwtClaimTypes.Role
                }
            };
            await _clientStore.CreateAsync(appClient);
        }

        var octoBotServiceSwaggerClient =
            await _clientStore.FindClientByIdAsync(CommonConstants.OctoBotServicesSwaggerClientId);
        if (octoBotServiceSwaggerClient == null)
        {
            var appClient = new OctoClient
            {
                ClientId = CommonConstants.OctoBotServicesSwaggerClientId,

                ClientName = BotTexts.Backend_BotServices_UserSchema_Swagger_DisplayName,
                ClientUri = _octoBotServicesOptions.PublicUrl,

                AllowedGrantTypes = new[] { OidcConstants.GrantTypes.AuthorizationCode },

                RequirePkce = true,
                RequireClientSecret = false,

                AccessTokenType = AccessTokenType.Jwt,
                AllowAccessTokensViaBrowser = true,
                AlwaysIncludeUserClaimsInIdToken = true,

                RedirectUris =
                {
                    _octoBotServicesOptions.PublicUrl.EnsureEndsWith("/swagger/oauth2-redirect.html")
                },

                PostLogoutRedirectUris = { _octoBotServicesOptions.PublicUrl.EnsureEndsWith("/") },
                AllowedCorsOrigins = { _octoBotServicesOptions.PublicUrl.TrimEnd('/') },
                AllowedScopes =
                {
                    CommonConstants.Scopes.OpenId,
                    CommonConstants.Scopes.Profile,
                    CommonConstants.Scopes.Email,
                    JwtClaimTypes.Role,
                    CommonConstants.BotApiFullAccess,
                    CommonConstants.BotApiReadOnly
                }
            };
            await _clientStore.CreateAsync(appClient);
        }
    }
}
