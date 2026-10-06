using System.Text.Json;
using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Secrets;

/// <summary>
///     In-memory <see cref="ISecretSweepRunStore" /> for tests. Stores JSON copies so a caller cannot change a
///     stored run by keeping a reference (like the real store).
/// </summary>
internal sealed class InMemorySecretSweepRunStore : ISecretSweepRunStore
{
    private readonly Dictionary<string, List<string>> _runs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public Task UpsertAsync(string tenantId, SecretSweepRunDto run)
    {
        Mutate(tenantId, runs =>
        {
            var index = runs.FindIndex(r => r.RunId == run.RunId);
            if (index >= 0)
            {
                HangfireSecretSweepRunStore.KeepDumpDeletion(runs[index], run);
                runs[index] = run;
            }
            else
            {
                runs.Insert(0, run);
            }

            if (runs.Count > ISecretSweepRunStore.MaxRunsPerTenant)
            {
                runs.RemoveRange(ISecretSweepRunStore.MaxRunsPerTenant, runs.Count - ISecretSweepRunStore.MaxRunsPerTenant);
            }

            return true;
        });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SecretSweepRunDto>> GetRunsAsync(string tenantId,
        int limit = ISecretSweepRunStore.MaxRunsPerTenant)
    {
        lock (_lock)
        {
            IReadOnlyList<SecretSweepRunDto> runs = Load(tenantId).Take(limit).ToList();
            return Task.FromResult(runs);
        }
    }

    public Task<SecretSweepRunDto?> UpdateAsync(string tenantId, string runId, Func<SecretSweepRunDto, bool> update)
    {
        SecretSweepRunDto? result = null;
        Mutate(tenantId, runs =>
        {
            var run = runs.FirstOrDefault(r => r.RunId == runId);
            if (run == null)
            {
                return false;
            }

            result = run;
            return update(run);
        });
        return Task.FromResult(result);
    }

    public Task<bool> MarkDumpDeletedAsync(string tenantId, string fileName, DateTime deletedAt, string? deletedBy)
    {
        var changed = false;
        Mutate(tenantId, runs =>
        {
            foreach (var run in runs.Where(r => r.Dump?.FileName == fileName && r.Dump.DeletedAt == null))
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

    private void Mutate(string tenantId, Func<List<SecretSweepRunDto>, bool> mutate)
    {
        lock (_lock)
        {
            var runs = Load(tenantId);
            if (mutate(runs))
            {
                _runs[tenantId] = runs.Select(r => JsonSerializer.Serialize(r, SecretSweepReportJson.Options)).ToList();
            }
        }
    }

    private List<SecretSweepRunDto> Load(string tenantId)
    {
        return _runs.TryGetValue(tenantId, out var json)
            ? json.Select(j => JsonSerializer.Deserialize<SecretSweepRunDto>(j, SecretSweepReportJson.Options)!).ToList()
            : [];
    }
}
