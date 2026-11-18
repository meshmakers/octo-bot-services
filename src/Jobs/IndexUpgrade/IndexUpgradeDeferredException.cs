using Hangfire.Common;
using Hangfire.States;

namespace Meshmakers.Octo.Backend.Jobs.IndexUpgrade;

/// <summary>
///     Thrown by an index upgrade job that could not do its work now and must run again later (global limit reached,
///     tenant busy with a CK import, <c>index_update_lock</c> held, yielded to a CK import). Never a failure:
///     <see cref="IndexUpgradeDeferAttribute" /> turns it into <see cref="ScheduledState" />.
/// </summary>
public sealed class IndexUpgradeDeferredException(string reason, TimeSpan delay) : Exception(reason)
{
    /// <summary>When the job runs again.</summary>
    public TimeSpan Delay { get; } = delay;
}

/// <summary>
///     State election filter of the index upgrade job (AB#9118): a job that ended with
///     <see cref="IndexUpgradeDeferredException" /> is scheduled again after its delay instead of failing. Its
///     <see cref="JobFilterAttribute.Order" /> 0 runs it before <c>AutomaticRetry</c> (order 20), which then sees no
///     failure, so a deferral never uses up a retry and a waiting tenant never ends <c>Failed</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class IndexUpgradeDeferAttribute : JobFilterAttribute, IElectStateFilter
{
    /// <summary>Constructor.</summary>
    public IndexUpgradeDeferAttribute()
    {
        Order = 0;
    }

    /// <inheritdoc />
    public void OnStateElection(ElectStateContext context)
    {
        if (context.CandidateState is FailedState { Exception: IndexUpgradeDeferredException deferred })
        {
            context.CandidateState = new ScheduledState(deferred.Delay) { Reason = deferred.Message };
        }
    }
}
