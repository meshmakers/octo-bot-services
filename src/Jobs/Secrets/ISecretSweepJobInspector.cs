namespace Meshmakers.Octo.Backend.Jobs.Secrets;

/// <summary>
///     State of the background job behind a secret sweep run, as far as the bot can tell after a restart
///     (AB#5539).
/// </summary>
public enum SecretSweepJobState
{
    /// <summary>
    ///     No job with this id exists (expired, deleted, or the run was not started by a job).
    /// </summary>
    Unknown = 0,

    /// <summary>
    ///     The job exists and is not processing (succeeded, failed, deleted, enqueued again, scheduled, ...).
    /// </summary>
    NotProcessing = 1,

    /// <summary>
    ///     The job is processing on a server that sent a heartbeat since this service started.
    /// </summary>
    ProcessingOnLiveServer = 2,

    /// <summary>
    ///     The job is marked processing, but its server is gone or has not sent a heartbeat since this service
    ///     started (the process ended without updating the job).
    /// </summary>
    ProcessingOnDeadServer = 3
}

/// <summary>
///     Looks up the background job state of a secret sweep run (the run id is the job id).
/// </summary>
public interface ISecretSweepJobInspector
{
    /// <summary>
    ///     Returns the state of job <paramref name="jobId" />. A processing server counts as alive when its last
    ///     heartbeat is not older than <paramref name="aliveSince" /> (UTC).
    /// </summary>
    SecretSweepJobState GetState(string jobId, DateTime aliveSince);
}
