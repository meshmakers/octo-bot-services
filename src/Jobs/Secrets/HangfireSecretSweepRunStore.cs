using System.Text.Json;
using Hangfire;
using Hangfire.Storage;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.Jobs.Secrets;

/// <summary>
///     <see cref="ISecretSweepRunStore" /> on the Hangfire job storage (the bot's job database), like
///     <see cref="HangfireSecretSweepReportStore" />: one hash per tenant holding the run list as one JSON
///     array (at most <see cref="ISecretSweepRunStore.MaxRunsPerTenant" /> small entries), changed under a
///     per-tenant distributed lock so concurrent writers (sweep, dump delete, cleanup) do not lose updates.
/// </summary>
public class HangfireSecretSweepRunStore : ISecretSweepRunStore
{
    internal const string RunsField = "runs";
    internal const string LockPrefix = "octo:secret-sweep:runs-lock:";
    internal static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<JobStorage> _storage;

    /// <summary>
    ///     Constructor using <see cref="JobStorage.Current" />.
    /// </summary>
    public HangfireSecretSweepRunStore() : this(() => JobStorage.Current)
    {
    }

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="storage">Resolves the job storage</param>
    public HangfireSecretSweepRunStore(Func<JobStorage> storage)
    {
        _storage = storage;
    }

    /// <inheritdoc />
    public Task UpsertAsync(string tenantId, SecretSweepRunDto run)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(run.RunId);
        Mutate(tenantId, runs =>
        {
            var index = runs.FindIndex(r => string.Equals(r.RunId, run.RunId, StringComparison.Ordinal));
            if (index >= 0)
            {
                runs[index] = run;
            }
            else
            {
                runs.Insert(0, run);
            }

            if (runs.Count > ISecretSweepRunStore.MaxRunsPerTenant)
            {
                runs.RemoveRange(ISecretSweepRunStore.MaxRunsPerTenant,
                    runs.Count - ISecretSweepRunStore.MaxRunsPerTenant);
            }

            return true;
        });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SecretSweepRunDto>> GetRunsAsync(string tenantId,
        int limit = ISecretSweepRunStore.MaxRunsPerTenant)
    {
        using var connection = _storage().GetConnection();
        IReadOnlyList<SecretSweepRunDto> runs = Read(connection, NormalizeTenant(tenantId))
            .Take(Math.Max(0, limit))
            .ToList();
        return Task.FromResult(runs);
    }

    /// <inheritdoc />
    public Task<SecretSweepRunDto?> UpdateAsync(string tenantId, string runId, Func<SecretSweepRunDto, bool> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        SecretSweepRunDto? result = null;
        Mutate(tenantId, runs =>
        {
            var run = runs.FirstOrDefault(r => string.Equals(r.RunId, runId, StringComparison.Ordinal));
            if (run == null)
            {
                return false;
            }

            result = run;
            return update(run);
        });
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<bool> MarkDumpDeletedAsync(string tenantId, string fileName, DateTime deletedAt, string? deletedBy)
    {
        var changed = false;
        Mutate(tenantId, runs =>
        {
            foreach (var run in runs.Where(r => r.Dump != null &&
                                                string.Equals(r.Dump.FileName, fileName, StringComparison.Ordinal) &&
                                                r.Dump.DeletedAt == null))
            {
                run.Dump!.DeletedAt = deletedAt;
                run.Dump.DeletedBy = deletedBy;
                run.Dump.Exists = false;
                changed = true;
            }

            return changed;
        });
        return Task.FromResult(changed);
    }

    internal static string RunsKey(string normalizedTenantId) => $"octo:secret-sweep:runs:{normalizedTenantId}";

    private void Mutate(string tenantId, Func<List<SecretSweepRunDto>, bool> mutate)
    {
        var tenantKey = NormalizeTenant(tenantId);
        using var connection = _storage().GetConnection();
        using var held = connection.AcquireDistributedLock(LockPrefix + tenantKey, LockTimeout);

        var runs = Read(connection, tenantKey);
        if (!mutate(runs))
        {
            return;
        }

        var json = JsonSerializer.Serialize(runs, SecretSweepReportJson.Options);
        using var transaction = connection.CreateWriteTransaction();
        transaction.SetRangeInHash(RunsKey(tenantKey), [new KeyValuePair<string, string>(RunsField, json)]);
        transaction.Commit();
    }

    private static string NormalizeTenant(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return tenantId.ToLowerInvariant();
    }

    private static List<SecretSweepRunDto> Read(IStorageConnection connection, string normalizedTenantId)
    {
        var entries = connection.GetAllEntriesFromHash(RunsKey(normalizedTenantId));
        if (entries == null || !entries.TryGetValue(RunsField, out var json) || string.IsNullOrEmpty(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<SecretSweepRunDto>>(json, SecretSweepReportJson.Options) ?? [];
    }
}
