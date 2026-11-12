using Meshmakers.Octo.Backend.BotServices.Services;

namespace Meshmakers.Octo.Backend.BotServices.Methods;

/// <summary>Declared errors and error parameters the job methods of the bot service share (AB#8447).</summary>
public static class BotJobMethodErrors
{
    /// <summary>Declared error of Restore-1 and ImportArchiveData-1: the upload is not usable.</summary>
    public const string UploadNotFound = "UPLOAD_NOT_FOUND";

    /// <summary>Error parameter naming the Hangfire job.</summary>
    public const string JobIdParameter = JobOperationRelay.JobIdParameter;

    /// <summary>Error parameter with a reason the caller can act on.</summary>
    public const string ReasonParameter = "reason";

    /// <summary><see cref="ReasonParameter" /> of <see cref="UploadNotFound" />: no upload with this id for the tenant.</summary>
    public const string UploadMissingReason = "not-found";

    /// <summary><see cref="ReasonParameter" /> of <see cref="UploadNotFound" />: the tus upload is not complete.</summary>
    public const string UploadIncompleteReason = "incomplete";

    /// <summary><see cref="ReasonParameter" /> of <see cref="UploadNotFound" />: the upload is empty.</summary>
    public const string UploadEmptyReason = "empty";

    /// <summary>
    ///     <see cref="ReasonParameter" /> of a failed job: it did not start because another job of the same kind held the
    ///     lock for longer than the lock timeout (10 min); try again later.
    /// </summary>
    public const string AnotherJobRunningReason = "another-job-running";

    /// <summary><see cref="ReasonParameter" /> of a failed job: the job no longer exists in the job storage.</summary>
    public const string JobMissingReason = "job-missing";

    /// <summary>The reason parameter of an <see cref="UploadNotFound" /> answer.</summary>
    public static (string Name, string Value) UploadReason(StagedUploadProblem problem) =>
        (ReasonParameter, problem switch
        {
            StagedUploadProblem.Incomplete => UploadIncompleteReason,
            StagedUploadProblem.Empty => UploadEmptyReason,
            _ => UploadMissingReason
        });

    /// <summary>The parameters of a failed or interrupted job: the job id and, when known, a reason.</summary>
    internal static (string Name, string Value)[] FailureParameters(JobOperationEnd end, string jobId) =>
        end.Kind switch
        {
            JobOperationEndKind.LockTimeout => [(JobIdParameter, jobId), (ReasonParameter, AnotherJobRunningReason)],
            JobOperationEndKind.Missing => [(JobIdParameter, jobId), (ReasonParameter, JobMissingReason)],
            _ => [(JobIdParameter, jobId)]
        };
}

/// <summary>
///     Options of the restore and archive data methods (section <c>Methods:BotJobs</c>, env
///     <c>OCTO_Methods__BotJobs__*</c>; AB#8447). The dump keeps <see cref="RepositoryDumpMethodOptions" />.
/// </summary>
public sealed class BotJobMethodOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Methods:BotJobs";

    /// <summary>How often the work reads the job state and progress. Default 2 s, at least 100 ms.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
}
