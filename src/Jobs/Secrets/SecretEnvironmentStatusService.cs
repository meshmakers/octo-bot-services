using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Runtime.Engine.Secrets;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.Jobs.Secrets;

/// <inheritdoc />
public class SecretEnvironmentStatusService(
    ISecretAttributeProtector protector,
    IOptions<SecretEncryptionOptions> encryptionOptions,
    IOptions<SecretSweepJobOptions> sweepOptions,
    ISecretSweepRunStore runStore,
    TimeProvider? timeProvider = null) : ISecretEnvironmentStatusService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<SecretEnvironmentStatusDto> GetStatusAsync(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var encryption = encryptionOptions.Value;
        var sweep = sweepOptions.Value;
        var configured = protector.IsConfigured;
        var strictSince = sweep.StrictModeSince;

        var runs = await runStore.GetRunsAsync(tenantId);
        var lastVerify = runs
            .Where(r => r.Mode == SecretSweepModeDto.Verify && r.CompletedAt != null &&
                        r.Outcome is SecretSweepOutcomeDto.Succeeded or SecretSweepOutcomeDto.CompletedWithFailures)
            .Max(r => r.CompletedAt);

        return new SecretEnvironmentStatusDto
        {
            KeyRingConfigured = configured,
            ActiveKeyId = configured ? protector.ActiveKeyId : null,
            // Key ids only - never key material.
            KnownKeyIds = configured
                ? encryption.Keys
                    .Where(k => !string.IsNullOrWhiteSpace(k.Value) && protector.IsKnownKeyId(k.Key))
                    .Select(k => k.Key)
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [],
            LegacyV1KeyConfigured = !string.IsNullOrWhiteSpace(encryption.LegacyV1Key),
            // The engine's strict mode (legacy clear text no longer readable) or the bot's strict-mode date
            // (legacy values are a violation from then on).
            StrictMode = protector.IsStrictMode || (strictSince != null && _time.GetUtcNow() >= strictSince.Value),
            StrictModeSince = strictSince?.UtcDateTime,
            RecurringVerifyCron = string.IsNullOrWhiteSpace(sweep.VerifyCron) ? null : sweep.VerifyCron,
            LastVerifyAt = lastVerify
        };
    }
}
