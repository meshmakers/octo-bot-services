using Hangfire.Server;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs.Operations;

/// <summary>
///     Names of the Hangfire job parameters of a job that an asynchronous CK method started (AB#8447): restore, archive
///     data export and archive data import. The dump (AB#8445) keeps its own names (<see cref="DumpJobParameters" />), so a
///     dump job enqueued by an older bot service is still read correctly.
/// </summary>
public static class OperationJobParameters
{
    /// <summary>The operation id of the method call that started the job (same name as the dump's).</summary>
    public const string OperationId = DumpJobParameters.OperationId;

    /// <summary>The latest <see cref="DumpProgress" /> as JSON.</summary>
    public const string Progress = "OctoOperationProgress";

    /// <summary>Set when the job started once; a second start of the same job (a requeue after a restart) is refused.</summary>
    public const string Started = "OctoOperationStarted";

    /// <summary>
    ///     Why the job refused to run: <see cref="DumpJobParameters.InterruptedOutcome" /> or
    ///     <see cref="DumpJobParameters.OperationClosedOutcome" />.
    /// </summary>
    public const string Outcome = "OctoOperationOutcome";

    /// <summary>A small JSON summary of the job's result that the method handler turns into the operation result.</summary>
    public const string Result = "OctoOperationResult";
}

/// <summary>The set of job parameter names one kind of method-started job uses (AB#8447).</summary>
/// <param name="OperationId">Parameter with the operation id.</param>
/// <param name="Progress">Parameter with the latest progress (JSON of <see cref="DumpProgress" />).</param>
/// <param name="Started">Parameter set at the first start.</param>
/// <param name="Outcome">Parameter with the reason a start was refused.</param>
public sealed record OperationJobParameterNames(string OperationId, string Progress, string Started, string Outcome)
{
    /// <summary>The names of the dump (AB#8445).</summary>
    public static OperationJobParameterNames Dump { get; } = new(DumpJobParameters.OperationId,
        DumpJobParameters.Progress, DumpJobParameters.Started, DumpJobParameters.Outcome);

    /// <summary>The names of restore, archive export and archive import (AB#8447).</summary>
    public static OperationJobParameterNames Generic { get; } = new(OperationJobParameters.OperationId,
        OperationJobParameters.Progress, OperationJobParameters.Started, OperationJobParameters.Outcome);
}

/// <summary>
///     The at-most-once entry of a job that an asynchronous CK method started (OD-5 of AB#6927; AB#8445 for the dump,
///     AB#8447 for restore and archive data). Hangfire requeues a job whose server stopped or died, and
///     <c>AutomaticRetry(Attempts = 0)</c> does not cover that; so a job that started before is refused
///     (<see cref="DumpJobParameters.InterruptedOutcome" />), and so is a job whose operation was ended in the meantime
///     (<see cref="DumpJobParameters.OperationClosedOutcome" />): the caller was told the outcome is unknown, running now
///     would be a second, unrequested run.
/// </summary>
public static class OperationJobGuard
{
    /// <summary>
    ///     Refuses a second start and a start for a closed operation (throws <see cref="JobFailedException" /> after writing
    ///     the outcome parameter), marks the first start, and returns the sink that writes the job's progress.
    /// </summary>
    /// <param name="performContext">Injected by Hangfire; <c>null</c> in a direct call (no parameters, no progress).</param>
    /// <param name="names">The parameter names of the job kind.</param>
    /// <param name="operationGate">The gate on the operation store; <c>null</c> skips the operation check.</param>
    /// <param name="tenantId">Tenant of the job.</param>
    /// <param name="operationId">The operation id the job was enqueued with.</param>
    /// <param name="work">What the job does, for log lines and messages (e.g. <c>"restore"</c>).</param>
    /// <param name="logger">Logger of the job.</param>
    /// <param name="cancellationToken">Cancellation token of the job.</param>
    public static async Task<IDumpProgressSink> EnterAsync(PerformContext? performContext,
        OperationJobParameterNames names, IDumpOperationGate? operationGate, string tenantId, string operationId,
        string work, ILogger logger, CancellationToken cancellationToken)
    {
        if (performContext != null)
        {
            if (performContext.GetJobParameter<bool>(names.Started))
            {
                logger.LogWarning(
                    "The {Work} of tenant '{TenantId}' for method operation '{OperationId}' (job '{JobId}') was " +
                    "interrupted and is not started again", work, tenantId, operationId, performContext.BackgroundJob.Id);
                performContext.SetJobParameter(names.Outcome, DumpJobParameters.InterruptedOutcome);
                throw new JobFailedException(
                    $"The {work} of tenant '{tenantId}' was interrupted (the bot service stopped while it ran) and is " +
                    $"not started again: a {work} started by a method call runs at most once.");
            }

            performContext.SetJobParameter(names.Started, true);
        }

        if (operationGate != null &&
            !await operationGate.IsOperationOpenAsync(tenantId, operationId, cancellationToken))
        {
            logger.LogWarning(
                "The {Work} of tenant '{TenantId}' is not started: method operation '{OperationId}' is no longer open",
                work, tenantId, operationId);
            performContext?.SetJobParameter(names.Outcome, DumpJobParameters.OperationClosedOutcome);
            throw new JobFailedException(
                $"The {work} of tenant '{tenantId}' is not started: its method operation was already ended.");
        }

        return performContext != null
            ? new JobParameterProgressSink(performContext, names.Progress, logger)
            : NullDumpProgressSink.Instance;
    }

    /// <summary>
    ///     Writes the result summary of the job (<see cref="OperationJobParameters.Result" />, JSON). A storage problem is
    ///     logged and never fails the job: the handler then completes without the summary.
    /// </summary>
    public static void WriteResult(PerformContext? performContext, string resultJson, ILogger logger)
    {
        if (performContext == null)
        {
            return;
        }

        try
        {
            performContext.SetJobParameter(OperationJobParameters.Result, resultJson);
        }
        catch (Exception e)
        {
            logger.LogWarning("Result summary of job '{JobId}' not stored ({ExceptionType})",
                performContext.BackgroundJob.Id, e.GetType().Name);
        }
    }

    /// <summary>
    ///     Writes the progress as a job parameter (shared job storage, so the handler reads it on any replica). A storage
    ///     problem is logged and never fails the job.
    /// </summary>
    private sealed class JobParameterProgressSink(PerformContext performContext, string parameterName, ILogger logger)
        : IDumpProgressSink
    {
        public void Report(DumpProgress progress)
        {
            try
            {
                performContext.SetJobParameter(parameterName, progress.ToJson());
            }
            catch (Exception e)
            {
                logger.LogWarning("Progress of job '{JobId}' not stored ({ExceptionType})",
                    performContext.BackgroundJob.Id, e.GetType().Name);
            }
        }
    }
}
