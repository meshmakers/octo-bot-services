using Hangfire;
using Hangfire.States;

namespace Meshmakers.Octo.Backend.Jobs.Secrets;

/// <summary>
///     <see cref="ISecretSweepJobInspector" /> on the Hangfire job storage.
/// </summary>
public class HangfireSecretSweepJobInspector : ISecretSweepJobInspector
{
    internal const string ServerIdKey = "ServerId";

    private readonly Func<JobStorage> _storage;

    /// <summary>
    ///     Constructor using <see cref="JobStorage.Current" />.
    /// </summary>
    public HangfireSecretSweepJobInspector() : this(() => JobStorage.Current)
    {
    }

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="storage">Resolves the job storage</param>
    public HangfireSecretSweepJobInspector(Func<JobStorage> storage)
    {
        _storage = storage;
    }

    /// <inheritdoc />
    public SecretSweepJobState GetState(string jobId, DateTime aliveSince)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return SecretSweepJobState.Unknown;
        }

        var storage = _storage();
        string? serverId;
        using (var connection = storage.GetConnection())
        {
            Hangfire.Storage.JobData? job;
            try
            {
                job = connection.GetJobData(jobId);
            }
            catch (Exception e) when (e is FormatException or ArgumentException)
            {
                // Not a job id of this storage (e.g. a random run id of a run started outside a job).
                return SecretSweepJobState.Unknown;
            }

            if (job == null)
            {
                return SecretSweepJobState.Unknown;
            }

            if (!string.Equals(job.State, ProcessingState.StateName, StringComparison.OrdinalIgnoreCase))
            {
                return SecretSweepJobState.NotProcessing;
            }

            var state = connection.GetStateData(jobId);
            serverId = state?.Data != null && state.Data.TryGetValue(ServerIdKey, out var id) ? id : null;
        }

        if (string.IsNullOrEmpty(serverId))
        {
            return SecretSweepJobState.ProcessingOnDeadServer;
        }

        var alive = storage.GetMonitoringApi().Servers().Any(s =>
            string.Equals(s.Name, serverId, StringComparison.Ordinal) &&
            s.Heartbeat != null && ToUtc(s.Heartbeat.Value) >= aliveSince);
        return alive ? SecretSweepJobState.ProcessingOnLiveServer : SecretSweepJobState.ProcessingOnDeadServer;
    }

    private static DateTime ToUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}
