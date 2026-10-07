using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Meshmakers.Octo.Backend.Jobs.Secrets;

/// <summary>
///     Strict-mode alert metric of the secret sweep (AB#5539, concept §5.3): the observable gauge
///     <c>octo.secrets.strict_mode.violations{tenant}</c> reports, per tenant swept while strict mode is in
///     force, the legacy values (clear text plus <c>enc:v1</c>) still stored after the sweep. Alert on
///     <c>&gt; 0</c>.
/// </summary>
/// <remarks>
///     The instrument lives on a meter named <c>Meshmakers.Octo.Secrets</c> - the name the engine's
///     <c>SecretDiagnostics</c> uses and that <c>AddObservability()</c> (octo-common-services
///     <c>ObservabilityBuilder</c>) registers - so no extra meter registration is needed. Meters are matched
///     by name; two <see cref="Meter" /> instances with the same name are both exported. The per-form gauge
///     <c>octo.secrets.values{tenant,model,form,kid}</c> itself is recorded by the engine's sweep.
/// </remarks>
public static class SecretSweepStrictModeDiagnostics
{
    /// <summary>
    ///     Meter name (same as the engine's <c>SecretDiagnostics.MeterName</c>).
    /// </summary>
    public const string MeterName = "Meshmakers.Octo.Secrets";

    /// <summary>
    ///     Instrument name.
    /// </summary>
    public const string ViolationsInstrumentName = "octo.secrets.strict_mode.violations";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly ConcurrentDictionary<string, long> Violations = new(StringComparer.OrdinalIgnoreCase);

    // ReSharper disable once UnusedMember.Local - the instrument lives as long as the meter.
    private static readonly ObservableGauge<long> ViolationsGauge = Meter.CreateObservableGauge(
        ViolationsInstrumentName,
        () => Violations.Select(v => new Measurement<long>(v.Value, new TagList { { "tenant", v.Key } })),
        unit: "{value}",
        description: "Secret values still stored as clear text or enc:v1 after the last secret sweep of a tenant " +
                     "while strict mode is in force (alert when > 0).");

    /// <summary>
    ///     Records the legacy values found in strict mode for a tenant (0 = compliant).
    /// </summary>
    public static void Record(string tenantId, long legacyValues)
    {
        Violations[tenantId] = legacyValues;
    }

    /// <summary>
    ///     Removes a tenant from the gauge (strict mode not in force).
    /// </summary>
    public static void Clear(string tenantId)
    {
        Violations.TryRemove(tenantId, out _);
    }

    /// <summary>
    ///     Current value for a tenant, <c>null</c> when not reported (tests, diagnostics).
    /// </summary>
    public static long? Get(string tenantId)
    {
        return Violations.TryGetValue(tenantId, out var value) ? value : null;
    }
}
