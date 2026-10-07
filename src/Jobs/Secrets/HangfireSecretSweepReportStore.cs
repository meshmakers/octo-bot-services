using System.Text.Json;
using System.Text.Json.Serialization;
using Hangfire;

namespace Meshmakers.Octo.Backend.Jobs.Secrets;

/// <summary>
///     JSON form of secret sweep reports (store and API): web defaults (camelCase), enums as names.
/// </summary>
public static class SecretSweepReportJson
{
    /// <summary>
    ///     Serializer options.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}

/// <summary>
///     <see cref="ISecretSweepReportStore" /> on the Hangfire job storage (the bot's job database): one hash
///     per tenant plus a set of the tenants that have a report. No new collection, no new connection.
/// </summary>
public class HangfireSecretSweepReportStore : ISecretSweepReportStore
{
    internal const string TenantSetKey = "octo:secret-sweep:tenants";
    internal const string ReportField = "report";

    private readonly Func<JobStorage> _storage;

    /// <summary>
    ///     Constructor using <see cref="JobStorage.Current" />.
    /// </summary>
    public HangfireSecretSweepReportStore() : this(() => JobStorage.Current)
    {
    }

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="storage">Resolves the job storage</param>
    public HangfireSecretSweepReportStore(Func<JobStorage> storage)
    {
        _storage = storage;
    }

    /// <inheritdoc />
    public Task SaveAsync(SecretSweepReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var tenantKey = NormalizeTenant(report.TenantId);
        var json = JsonSerializer.Serialize(report, SecretSweepReportJson.Options);

        using var connection = _storage().GetConnection();
        using var transaction = connection.CreateWriteTransaction();
        transaction.SetRangeInHash(ReportKey(tenantKey), [new KeyValuePair<string, string>(ReportField, json)]);
        transaction.AddToSet(TenantSetKey, tenantKey);
        transaction.Commit();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<SecretSweepReport?> GetLastAsync(string tenantId)
    {
        using var connection = _storage().GetConnection();
        return Task.FromResult(Read(connection, NormalizeTenant(tenantId)));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SecretSweepReport>> GetAllLastAsync()
    {
        using var connection = _storage().GetConnection();
        var reports = connection.GetAllItemsFromSet(TenantSetKey)
            .OrderBy(t => t, StringComparer.Ordinal)
            .Select(t => Read(connection, t))
            .OfType<SecretSweepReport>()
            .ToList();
        return Task.FromResult<IReadOnlyList<SecretSweepReport>>(reports);
    }

    internal static string ReportKey(string normalizedTenantId) => $"octo:secret-sweep:report:{normalizedTenantId}";

    private static string NormalizeTenant(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return tenantId.ToLowerInvariant();
    }

    private static SecretSweepReport? Read(Hangfire.Storage.IStorageConnection connection, string normalizedTenantId)
    {
        var entries = connection.GetAllEntriesFromHash(ReportKey(normalizedTenantId));
        if (entries == null || !entries.TryGetValue(ReportField, out var json) || string.IsNullOrEmpty(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<SecretSweepReport>(json, SecretSweepReportJson.Options);
    }
}
