using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.StreamData;

namespace Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;

/// <summary>
///     Builds a per-job <see cref="IStreamDataServicesClient"/> bound to the asset-repo endpoint and
///     authenticated with the operator's bearer token captured at enqueue time. Mirrors the MCP
///     service's <c>OctoServiceClientFactory</c> pattern: the StreamData REST is hosted on the
///     Asset Repository endpoint and <c>tenantId</c> is passed per-call, so one client instance can
///     serve the job's single tenant. Archive data export/import concept (AB#4230) §5.2.
/// </summary>
public interface IArchiveDataClientFactory
{
    /// <summary>
    ///     Creates a StreamData services client for the given tenant, authenticated with the supplied
    ///     bearer access token.
    /// </summary>
    /// <param name="tenantId">The tenant whose archive is being exported/imported.</param>
    /// <param name="accessToken">The raw bearer access token (no <c>Bearer </c> prefix).</param>
    IStreamDataServicesClient Create(string tenantId, string accessToken);
}
