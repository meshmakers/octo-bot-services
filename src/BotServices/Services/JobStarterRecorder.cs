using System.Security.Claims;

namespace Meshmakers.Octo.Backend.BotServices.Services;

/// <summary>
///     Records who started a job, as Hangfire job parameters, best effort (AB#5070).
/// </summary>
/// <remarks>
///     🔴 <b>Not an authorization input.</b> The binding that decides access is the tenant, read from the job's
///     arguments (<see cref="JobTenantBinding" />) — binding an artifact to the starting subject would lock out a
///     second administrator of the same tenant and make a dump started by CI unreachable for every human. These
///     parameters exist so that a later, finer rule (a "only the starter may fetch it" mode, an audit answer to "who
///     took this backup") does not need a data migration for jobs that already ran. A failure to write them must
///     never fail the enqueue that already succeeded — the job exists at this point, and its id has to reach the
///     caller.
/// </remarks>
internal static class JobStarterRecorder
{
    public static void Record(IJobStorageAccessor jobStorage, ClaimsPrincipal user, string jobId, string tenantId,
        ILogger logger)
    {
        try
        {
            var subject = user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!string.IsNullOrEmpty(subject))
            {
                jobStorage.SetJobParameter(jobId, JobTenantBinding.StartedBySubjectParameter, subject);
            }

            var clientId = user.FindFirstValue("client_id");
            if (!string.IsNullOrEmpty(clientId))
            {
                jobStorage.SetJobParameter(jobId, JobTenantBinding.StartedByClientIdParameter, clientId);
            }

            jobStorage.SetJobParameter(jobId, JobTenantBinding.StartedForTenantParameter, tenantId);
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "Could not record the starting subject of job '{JobId}' for tenant '{TenantId}'; the job " +
                "itself was enqueued (AB#5070)",
                jobId, tenantId);
        }
    }
}
