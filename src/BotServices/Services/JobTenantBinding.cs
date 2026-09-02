using Hangfire.Common;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

namespace Meshmakers.Octo.Backend.BotServices.Services;

/// <summary>
///     Answers "which tenant does this Hangfire job belong to" from the job's own stored invocation
///     (AB#5070).
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>The binding is the job's arguments, not a side table.</b> Every job of this service
///         that can produce a downloadable artifact already takes its tenant as an argument —
///         <c>Run(string tenantId, …)</c> for dump, restore, archive export/import and the fixup run,
///         and a <see cref="CommandBaseRequest" /> carrying <c>TenantId</c> for the two runtime-model
///         exports that arrive over the bus. Hangfire persists those arguments with the job and
///         <c>JobDetails</c> hands them back deserialized, so the tenant can be read off the job
///         itself. That is both the cheapest binding (no extra write, no extra store, nothing to
///         migrate for jobs already in the queue) and the one that cannot be forged: it is the very
///         value the job ran with, so an artifact can never be attributed to a tenant other than the
///         one whose data produced it.
///     </para>
///     <para>
///         <b>Unknown means denied.</b> A <c>null</c> answer is returned for a job whose invocation
///         could not be deserialized, whose signature carries no tenant at all (the instance-wide
///         <c>ICleanupStaleFilesJob</c>), or whose tenant argument is empty. Callers must refuse such
///         a job rather than fall back to the caller-supplied tenant — falling back would restore
///         exactly the hole AB#5070 closes.
///     </para>
///     <para>
///         The starting <i>subject</i> is deliberately <b>not</b> part of this binding. Granularity is
///         the tenant: binding an artifact to the person who started the job would lock out a second
///         administrator of the same tenant and would make a dump started by CI unreachable for every
///         human. The subject is recorded alongside the job
///         (<see cref="Controllers.JobsControllerBase" />) so that a later, finer rule has the data it
///         would need.
///     </para>
/// </remarks>
public static class JobTenantBinding
{
    /// <summary>
    ///     The Hangfire job parameter carrying the <c>sub</c> claim of whoever started the job.
    /// </summary>
    public const string StartedBySubjectParameter = "OctoStartedBySubject";

    /// <summary>
    ///     The Hangfire job parameter carrying the <c>client_id</c> claim of whoever started the job.
    /// </summary>
    public const string StartedByClientIdParameter = "OctoStartedByClientId";

    /// <summary>
    ///     The Hangfire job parameter carrying the tenant the starting request addressed. Recorded for
    ///     diagnostics only — the authorization answer is always read from the job arguments below,
    ///     which are the values the job actually ran with.
    /// </summary>
    public const string StartedForTenantParameter = "OctoStartedForTenant";

    private const string TenantParameterName = "tenantId";

    /// <summary>
    ///     The tenant <paramref name="job" /> belongs to, or <c>null</c> when it cannot be determined.
    /// </summary>
    public static string? TryResolveTenantId(Job? job)
    {
        var method = job?.Method;
        var args = job?.Args;
        if (method == null || args == null)
        {
            return null;
        }

        var parameters = method.GetParameters();
        var count = Math.Min(parameters.Length, args.Count);

        // The common shape: a parameter literally called tenantId.
        for (var i = 0; i < count; i++)
        {
            if (string.Equals(parameters[i].Name, TenantParameterName, StringComparison.OrdinalIgnoreCase) &&
                args[i] is string tenantId && !string.IsNullOrWhiteSpace(tenantId))
            {
                return tenantId;
            }
        }

        // The bus-driven exports take the whole command request, whose base type carries the tenant.
        for (var i = 0; i < count; i++)
        {
            if (args[i] is CommandBaseRequest request && !string.IsNullOrWhiteSpace(request.TenantId))
            {
                return request.TenantId;
            }
        }

        return null;
    }
}
