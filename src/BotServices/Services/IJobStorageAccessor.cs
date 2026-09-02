using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;

namespace Meshmakers.Octo.Backend.BotServices.Services;

/// <summary>
///     The seam between the job controllers and Hangfire's ambient storage
///     (<see cref="JobStorage.Current" />).
/// </summary>
/// <remarks>
///     AB#5070 moved the artifact authorization into the controllers, and that decision is only worth
///     anything if it can be tested. The controllers used to reach for the static
///     <c>JobStorage.Current</c> directly, which cannot be substituted without setting process-wide
///     state — impossible to do safely from tests that run in parallel. This interface is deliberately
///     narrow: the two operations the controllers actually perform, nothing of Hangfire's monitoring
///     surface beyond them.
/// </remarks>
public interface IJobStorageAccessor
{
    /// <summary>
    ///     The details of <paramref name="jobId" />, or <c>null</c> when no such job exists.
    /// </summary>
    /// <remarks>
    ///     <see cref="JobDetailsDto.Job" /> is <c>null</c> when the stored invocation could not be
    ///     deserialized (unknown type or changed signature). Callers must treat that as "the tenant of
    ///     this job is unknown" and fail closed — see <see cref="JobTenantBinding" />.
    /// </remarks>
    JobDetailsDto? GetJobDetails(string jobId);

    /// <summary>
    ///     Writes a Hangfire job parameter, best effort.
    /// </summary>
    /// <remarks>
    ///     Used by <see cref="Controllers.JobsControllerBase" /> to record who started a job (AB#5070).
    ///     The values are plain strings rather than Hangfire's JSON convention, because nothing but
    ///     this service ever reads them back and Hangfire's typed accessor is never used on them.
    /// </remarks>
    void SetJobParameter(string jobId, string name, string value);
}

/// <summary>
///     <see cref="IJobStorageAccessor" /> over Hangfire's ambient <see cref="JobStorage.Current" />,
///     which is what the controllers used before AB#5070 and stays the production path.
/// </summary>
internal sealed class HangfireJobStorageAccessor : IJobStorageAccessor
{
    public JobDetailsDto? GetJobDetails(string jobId)
    {
        return JobStorage.Current.GetMonitoringApi().JobDetails(jobId);
    }

    public void SetJobParameter(string jobId, string name, string value)
    {
        using var connection = JobStorage.Current.GetConnection();
        connection.SetJobParameter(jobId, name, value);
    }
}
