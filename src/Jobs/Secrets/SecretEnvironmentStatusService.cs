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

        // The engine's parsed key ring is the truth: a LegacyV1Key that is set but invalid (not Base64 / 32 bytes)
        // is skipped by the engine, and enc:v1 values are then key missing - report it as not configured.
        var legacyV1KeyConfigured = !string.IsNullOrWhiteSpace(encryption.LegacyV1Key) &&
                                    protector.IsLegacyV1KeyConfigured;
        var warnings = new List<string>();
        if (!configured)
        {
            // AB#5534: prominent in the UI - writes fail, a restore only classifies (key-free Verify).
            warnings.Add(SecretEnvironmentWarningCodes.NoKeyRing);
        }

        if (!legacyV1KeyConfigured && LastRunFoundEncV1(runs))
        {
            warnings.Add(SecretEnvironmentWarningCodes.NoLegacyV1Key);
        }

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
            LegacyV1KeyConfigured = legacyV1KeyConfigured,
            // The engine's strict mode (legacy clear text no longer readable) or the bot's strict-mode date
            // (legacy values are a violation from then on).
            StrictMode = protector.IsStrictMode || (strictSince != null && _time.GetUtcNow() >= strictSince.Value),
            StrictModeSince = strictSince?.UtcDateTime,
            RecurringVerifyCron = string.IsNullOrWhiteSpace(sweep.VerifyCron) ? null : sweep.VerifyCron,
            LastVerifyAt = lastVerify,
            Warnings = warnings
        };
    }

    /// <summary>
    ///     Cheap check from the run history (no scan): the tenant's latest completed sweep found <c>enc:v1</c>
    ///     values - counted as <c>EncV1</c> (legacy key present at the time) or as key missing with key id
    ///     <see cref="SecretValueStates.LegacyV1KeyId" /> (no legacy key). No run = no warning.
    /// </summary>
    private static bool LastRunFoundEncV1(IEnumerable<SecretSweepRunDto> runs)
    {
        var last = runs
            .Where(r => r.CompletedAt != null &&
                        r.Outcome is SecretSweepOutcomeDto.Succeeded or SecretSweepOutcomeDto.CompletedWithFailures)
            .MaxBy(r => r.CompletedAt);
        // The state after the run (AB#5539: Totals are the forms as found, TotalsAfter the follow-up Verify).
        var totals = last?.TotalsAfter ?? last?.Totals;
        if (totals == null)
        {
            return false;
        }

        return totals.EncV1 > 0 ||
               (totals.UnknownKeyIdByKeyId != null &&
                totals.UnknownKeyIdByKeyId.Any(p =>
                    string.Equals(p.Key, SecretValueStates.LegacyV1KeyId, StringComparison.OrdinalIgnoreCase) &&
                    p.Value > 0));
    }
}
