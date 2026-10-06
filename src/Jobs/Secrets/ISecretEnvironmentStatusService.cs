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
    ///     mode, the recurring Verify schedule and the last Verify run of <paramref name="tenantId" />, plus
    ///     <c>warnings</c> (AB#5534): <c>NoKeyRing</c> when no key ring is configured, <c>NoLegacyV1Key</c> when the
    ///     legacy key is missing although the tenant's latest completed sweep found <c>enc:v1</c> values,
    ///     <c>DumpKeyMissing</c> (AB#5559) when an encrypted dump in the artifact store needs a key id that is not in
    ///     the key ring; <c>requiredKeyIds</c> lists the key ids of all encrypted dumps of the instance.
    /// </summary>
    Task<BotSecretEnvironmentStatusDto> GetStatusAsync(string tenantId);
}
