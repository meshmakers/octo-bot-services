using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.Jobs.Secrets;

/// <summary>
///     Secret sweep run history and pre-sweep dump management of a tenant (AB#5544, contract §9). Dumps are
///     never handed out - they can only be deleted early.
/// </summary>
public interface ISecretSweepRunService
{
    /// <summary>
    ///     Returns up to <paramref name="limit" /> runs of <paramref name="tenantId" />, newest first, with the
    ///     current state of their dump file (<see cref="SecretSweepDumpDto.Exists" />,
    ///     <see cref="SecretSweepDumpDto.SizeBytes" />).
    /// </summary>
    Task<IReadOnlyList<SecretSweepRunDto>> GetRunsAsync(string tenantId, int limit);

    /// <summary>
    ///     Deletes the pre-sweep dump of run <paramref name="runId" /> early and records who did it.
    /// </summary>
    /// <returns>
    ///     <see cref="SecretSweepDumpDeleteResultDto.Deleted" />, <see cref="SecretSweepDumpDeleteResultDto.NotFound" />
    ///     (unknown run or no dump) or <see cref="SecretSweepDumpDeleteResultDto.AlreadyDeleted" />.
    /// </returns>
    Task<SecretSweepDumpDeleteResultDto> DeleteDumpAsync(string tenantId, string runId, string? deletedBy);

    /// <summary>
    ///     Checks whether the pre-sweep dump of run <paramref name="runId" /> of <paramref name="tenantId" /> can be
    ///     restored (AB#5559): it exists, is not deleted, and its key id is in the key ring.
    /// </summary>
    Task<SecretSweepDumpRestoreCheck> CheckDumpRestorableAsync(string tenantId, string runId);
}
