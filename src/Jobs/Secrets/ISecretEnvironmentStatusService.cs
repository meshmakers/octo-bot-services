using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.Jobs.Secrets;

/// <summary>
///     Encryption status of the environment as seen from one tenant (AB#5544, contract §9,
///     <c>GET {tenantId}/v1/secrets/status</c>). Never contains key material.
/// </summary>
public interface ISecretEnvironmentStatusService
{
    /// <summary>
    ///     Returns the status: key ring of this service (the engine's <c>SecretEncryption</c> section), strict
    ///     mode, the recurring Verify schedule and the last Verify run of <paramref name="tenantId" />.
    /// </summary>
    Task<SecretEnvironmentStatusDto> GetStatusAsync(string tenantId);
}
