using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.StreamData;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;

namespace Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;

/// <summary>
///     Default <see cref="IArchiveDataClientFactory"/>. Constructs a <see cref="StreamDataServicesClient"/>
///     against a configured asset-repo endpoint with a one-shot access token, exactly like the MCP
///     service's factory. Archive data export/import concept (AB#4230) §5.2.
/// </summary>
public sealed class ArchiveDataClientFactory : IArchiveDataClientFactory
{
    private readonly string _assetServiceUrl;

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="assetServiceUrl">Base URL of the Asset Repository service (StreamData is hosted there).</param>
    public ArchiveDataClientFactory(string assetServiceUrl)
    {
        if (string.IsNullOrWhiteSpace(assetServiceUrl))
        {
            throw new InvalidOperationException(
                "Asset Repository service URL is not configured (Bot:AssetServiceUrl). " +
                "Archive data export/import jobs cannot reach the asset-repo StreamData endpoints.");
        }

        _assetServiceUrl = assetServiceUrl;
    }

    /// <inheritdoc />
    public IStreamDataServicesClient Create(string tenantId, string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException(
                "No access token was captured for the archive data job. The operator's bearer token must be " +
                "forwarded from the originating request so the bot can call the asset-repo StreamData endpoints.");
        }

        var options = new StreamDataServiceClientOptions
        {
            EndpointUri = _assetServiceUrl,
            TenantId = tenantId
        };

        return new StreamDataServicesClient(options, new ServiceClientAccessToken { AccessToken = accessToken });
    }
}
