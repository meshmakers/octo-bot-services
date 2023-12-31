namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Interface for a job that can be run by the service hook
/// </summary>
public interface IServiceHookJob
{
    /// <summary>
    /// Runs the job
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task Run(string tenantId, IBotCancellationToken? cancellationToken);
}