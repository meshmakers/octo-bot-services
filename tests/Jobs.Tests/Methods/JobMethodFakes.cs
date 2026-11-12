using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.Jobs.Jobs.Operations;
using Meshmakers.Octo.Services.Methods.Operations;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Methods;

/// <summary>Records the progress reports of an operation (AB#8447 handler tests).</summary>
internal sealed class RecordingOperationProgress : ICkMethodOperationProgress
{
    public string OperationId { get; } = Guid.NewGuid().ToString("N");

    public List<CkMethodOperationProgress> Reports { get; } = [];

    public Task ReportAsync(CkMethodOperationProgress progress, CancellationToken cancellationToken = default)
    {
        Reports.Add(progress);
        return Task.CompletedTask;
    }
}

/// <summary>
///     Job storage that answers one queued state (and progress) per read, with the generic parameter names of the
///     restore and archive data jobs (AB#8447); the job writes in Hangfire's JSON convention.
/// </summary>
internal sealed class GenericFakeJobStorage : IJobStorageAccessor
{
    public Queue<string> States { get; } = new();
    public Queue<DumpProgress> ProgressSequence { get; } = new();
    public Dictionary<string, string> Written { get; } = new();
    public string? Outcome { get; set; }
    public string? ResultJson { get; set; }
    public string? FailedExceptionType { get; set; }
    public bool Vanished { get; set; }

    public JobDetailsDto? GetJobDetails(string jobId)
    {
        if (Vanished)
        {
            return null;
        }

        var state = States.Count > 0 ? States.Dequeue() : "Processing";
        var properties = new Dictionary<string, string>(Written);
        if (ProgressSequence.Count > 0)
        {
            properties[OperationJobParameters.Progress] =
                SerializationHelper.Serialize(ProgressSequence.Dequeue().ToJson(), SerializationOption.User);
        }

        if (Outcome != null)
        {
            properties[OperationJobParameters.Outcome] = SerializationHelper.Serialize(Outcome, SerializationOption.User);
        }

        if (ResultJson != null && state == "Succeeded")
        {
            properties[OperationJobParameters.Result] = SerializationHelper.Serialize(ResultJson, SerializationOption.User);
        }

        var history = new List<StateHistoryDto>
        {
            new() { StateName = state, CreatedAt = DateTime.UtcNow, Data = new Dictionary<string, string>() }
        };
        if (FailedExceptionType != null)
        {
            history.Add(new StateHistoryDto
            {
                StateName = "Failed", CreatedAt = DateTime.UtcNow,
                Data = new Dictionary<string, string> { ["ExceptionType"] = FailedExceptionType }
            });
        }

        return new JobDetailsDto { Properties = properties, History = history };
    }

    public void SetJobParameter(string jobId, string name, string value) => Written[name] = value;
}

/// <summary>
///     A <see cref="PerformContext" /> whose job parameters live in a dictionary (Hangfire's JSON convention), recording
///     the progress the job writes under the generic name <see cref="OperationJobParameters.Progress" /> (AB#8447).
/// </summary>
internal sealed class GenericFakePerformContext
{
    private readonly Dictionary<string, string> _parameters = new();

    public GenericFakePerformContext(string jobId, Job job)
    {
        var connection = Substitute.For<IStorageConnection>();
        connection.When(c => c.SetJobParameter(jobId, Arg.Any<string>(), Arg.Any<string>()))
            .Do(call =>
            {
                var name = call.ArgAt<string>(1);
                var value = call.ArgAt<string>(2);
                _parameters[name] = value;
                if (name == OperationJobParameters.Progress)
                {
                    Progress.Add(DumpProgress.FromJson(SerializationHelper.Deserialize<string>(value))!);
                }
            });
        connection.GetJobParameter(jobId, Arg.Any<string>())
            .Returns(call => _parameters.GetValueOrDefault(call.ArgAt<string>(1)));
        Context = new PerformContext(Substitute.For<JobStorage>(), connection,
            new BackgroundJob(jobId, job, DateTime.UtcNow), Substitute.For<IJobCancellationToken>());
    }

    public PerformContext Context { get; }

    public List<DumpProgress> Progress { get; } = [];

    public void SetJobParameter(string name, object value) =>
        _parameters[name] = SerializationHelper.Serialize(value, SerializationOption.User);

    public T? GetJobParameter<T>(string name) =>
        _parameters.TryGetValue(name, out var raw)
            ? SerializationHelper.Deserialize<T>(raw, SerializationOption.User)
            : default;
}
