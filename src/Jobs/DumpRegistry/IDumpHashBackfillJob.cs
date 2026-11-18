using System.ComponentModel;
using Hangfire;

namespace Meshmakers.Octo.Backend.Jobs.DumpRegistry;

/// <summary>Registers the tenant dumps still held in the artifact store that have no registry entry.</summary>
public interface IDumpHashBackfillJob
{
    /// <summary>Runs the backfill once over every tenant dump of this instance.</summary>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns>The number of dumps registered by this run.</returns>
    [DisplayName("Register stored tenant dumps in the dump registry")]
    [AutomaticRetry(Attempts = 3, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(60 * 10)]
    Task<int> Run(IBotCancellationToken? cancellationToken);
}
