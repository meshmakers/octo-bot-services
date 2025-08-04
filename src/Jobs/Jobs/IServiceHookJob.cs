using System.ComponentModel;
using Hangfire;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
///     Interface for a job that can be run by the service hook
/// </summary>
public interface IServiceHookJob
{
    /// <summary>
    ///     Runs the job
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    [DisplayName("Checks for new job schedules for tenant '{0}'")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    Task Run(string tenantId, IBotCancellationToken? cancellationToken);
}