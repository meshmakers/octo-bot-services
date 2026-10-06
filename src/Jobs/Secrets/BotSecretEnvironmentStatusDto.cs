using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.Jobs.Secrets;

/// <summary>
///     <see cref="SecretEnvironmentStatusDto" /> plus the bot's key-id retention view (AB#5559). Serialized by
///     <c>GET {tenantId}/v1/secrets/status</c>; the additional JSON property <c>requiredKeyIds</c> is ignored by older
///     clients. Follow-up: move <see cref="RequiredKeyIds" /> into the SDK DTO.
/// </summary>
public class BotSecretEnvironmentStatusDto : SecretEnvironmentStatusDto
{
    /// <summary>
    ///     Key ids of all encrypted (<c>.octoenc</c>) dumps currently in the artifact store of this instance
    ///     (pre-sweep dumps, tenant dumps, staged restore uploads), read from their clear-text headers. Every one of
    ///     them must stay in the key ring until the newest dump encrypted with it has expired; a missing one is
    ///     reported as <see cref="BotSecretEnvironmentWarningCodes.DumpKeyMissing" />.
    /// </summary>
    public List<string> RequiredKeyIds { get; set; } = [];
}

/// <summary>
///     Warning codes the bot adds to <see cref="SecretEnvironmentWarningCodes" /> (AB#5559). Follow-up: move into
///     the SDK.
/// </summary>
public static class BotSecretEnvironmentWarningCodes
{
    /// <summary>
    ///     An encrypted dump in the artifact store needs a key id that is not in the key ring
    ///     (<see cref="BotSecretEnvironmentStatusDto.RequiredKeyIds" />): it can no longer be decrypted (restored or
    ///     downloaded). Put the key back into <c>SecretEncryption:Keys</c>, or accept the loss until the dump expires.
    /// </summary>
    public const string DumpKeyMissing = "DumpKeyMissing";
}
