using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.Jobs.Secrets;

/// <summary>
///     Keeps the run history of the secret sweep per tenant (AB#5544, contract §9): the last
///     <see cref="MaxRunsPerTenant" /> runs, newest first, including the state of their pre-sweep dump. Counts
///     only - never a value.
/// </summary>
public interface ISecretSweepRunStore
{
    /// <summary>
    ///     Runs kept per tenant; older runs are dropped when a new one is added.
    /// </summary>
    public const int MaxRunsPerTenant = 50;

    /// <summary>
    ///     Inserts <paramref name="run" /> as the newest run of <paramref name="tenantId" />, or replaces the run
    ///     with the same <see cref="SecretSweepRunDto.RunId" /> in place. A dump the stored run already marks as
    ///     deleted stays deleted (same file name), atomically with the replace.
    /// </summary>
    Task UpsertAsync(string tenantId, SecretSweepRunDto run);

    /// <summary>
    ///     Returns up to <paramref name="limit" /> runs of <paramref name="tenantId" />, newest first.
    /// </summary>
    Task<IReadOnlyList<SecretSweepRunDto>> GetRunsAsync(string tenantId, int limit = MaxRunsPerTenant);

    /// <summary>
    ///     Atomically (per tenant) applies <paramref name="update" /> to the run <paramref name="runId" />.
    ///     <paramref name="update" /> returns <c>true</c> when it changed the run, which is then stored.
    /// </summary>
    /// <returns><c>null</c> when the run is unknown, otherwise the run after <paramref name="update" />.</returns>
    Task<SecretSweepRunDto?> UpdateAsync(string tenantId, string runId, Func<SecretSweepRunDto, bool> update);

    /// <summary>
    ///     Records that the dump file <paramref name="fileName" /> of <paramref name="tenantId" /> was deleted
    ///     (expired or early). No-op when no run references the file or it is already marked deleted.
    /// </summary>
    /// <returns><c>true</c> when a run was updated.</returns>
    Task<bool> MarkDumpDeletedAsync(string tenantId, string fileName, DateTime deletedAt, string? deletedBy);
}
