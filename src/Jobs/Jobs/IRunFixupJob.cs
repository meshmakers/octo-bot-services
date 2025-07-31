using System.ComponentModel;
using Hangfire;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Run a fixup job for a tenant.
/// </summary>
public interface IRunFixupJob
{
    /// <summary>
    /// Runs the job that fixes up the data of a tenant.
    /// </summary>
    /// <param name="tenantId">The corresponding tenant id</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns></returns>
    [DisplayName("Runs the fixup jobs of tenant '{0}'")]
    [AutomaticRetry(Attempts = 0)]
    Task Run(string tenantId, IBotCancellationToken? cancellationToken);
}