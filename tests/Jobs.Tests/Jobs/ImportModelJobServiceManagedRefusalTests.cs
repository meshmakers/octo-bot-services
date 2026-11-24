using System.Reflection;
using System.Text;
using Hangfire;
using Meshmakers.Common.Shared.Services;
using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Common.DistributionEventHub.Payloads;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs;

/// <summary>
///     The bot's CK import jobs (ImportCk, ImportCkBatch) with a System.Identity file: the engine refuses every caller
///     import of a service-managed model (<see cref="ServiceManagedCkModelImportRefusedException" />, code
///     <c>CK_SERVICE_MANAGED_MODEL</c>), and the job fails with that coded error - no retry, no model content, the cache
///     streams kept. The tenant is a substitute that applies the engine's own predicate
///     (<see cref="ServiceManagedCkModels.IsServiceManaged" />); the refusal itself is tested in the engine.
/// </summary>
public class ImportModelJobServiceManagedRefusalTests
{
    private const string TenantId = "tenant-1";
    private const string ContentMarker = "secret-model-content-marker";

    private readonly IDistributedCacheService _cacheService = Substitute.For<IDistributedCacheService>();
    private readonly ITenantContext _tenantContext = Substitute.For<ITenantContext>();
    private readonly List<string> _imported = [];
    private readonly CapturingLogger<ImportCkModelCommand> _commandLogger = new();

    public ImportModelJobServiceManagedRefusalTests()
    {
        _tenantContext.ImportCkModelAsync(Arg.Any<CkCompiledModelRoot>()).Returns(call =>
        {
            var model = call.Arg<CkCompiledModelRoot>();
            if (ServiceManagedCkModels.IsServiceManaged(model.ModelId.Name))
            {
                throw ServiceManagedCkModelImportRefusedException.Create(model.ModelId.FullName);
            }

            _imported.Add(model.ModelId.FullName);
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task ImportCk_WithASystemIdentityFile_FailsWithTheCodedRefusal_AndKeepsTheCacheStream()
    {
        AddCacheStream("key-identity", "System.Identity-3.9.0");

        var refusal = await Assert.That(async () => await CreateJob().ImportCkAsync(TenantId, "key-identity", null))
            .Throws<ServiceManagedCkModelImportRefusedException>();

        await Assert.That(refusal!.Code).IsEqualTo("CK_SERVICE_MANAGED_MODEL");
        await Assert.That(refusal.Message).Contains("'System.Identity-3.9.0'");
        await Assert.That(refusal.Message).DoesNotContain(ContentMarker);
        await Assert.That(_imported).IsEmpty();
        await _cacheService.DidNotReceive().DeleteCacheStreamAsync(TenantId, "key-identity");

        var warning = _commandLogger.Entries.Single(e => e.Level == LogLevel.Warning);
        await Assert.That(warning.Message).Contains("System.Identity-3.9.0");
        await Assert.That(warning.Message).Contains("CK_SERVICE_MANAGED_MODEL");
        await Assert.That(warning.Message).DoesNotContain(ContentMarker);
        await Assert.That(_commandLogger.Entries.Any(e => e.Level == LogLevel.Error)).IsFalse();
    }

    /// <summary>
    ///     Batch rule (sequential, in dependency order, per model): the models before the refused one stay imported, the
    ///     refused one and every model after it are not imported, the job fails, and no cache stream is cleared.
    /// </summary>
    [Test]
    public async Task ImportCkBatch_PerModel_ImportsTheModelBefore_StopsAtTheRefusedSystemIdentity_ImportsNothingAfter()
    {
        AddCacheStream("key-1", "Customer.Model-1.0.0");
        AddCacheStream("key-2", "System.Identity-3.9.0");
        AddCacheStream("key-3", "Customer.Other-1.0.0");

        await Assert.That(async () =>
                await CreateJob().ImportCkBatchAsync(TenantId, ["key-1", "key-2", "key-3"], null))
            .Throws<ServiceManagedCkModelImportRefusedException>();

        await Assert.That(_imported).IsEquivalentTo(["Customer.Model-1.0.0"]);
        await _cacheService.DidNotReceive().GetCacheStreamByIdAsync(TenantId, "key-3");
        await _cacheService.DidNotReceive().DeleteCacheStreamAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task ImportCk_WithAnOrdinaryModel_StillImports()
    {
        AddCacheStream("key-ordinary", "Customer.System-1.0.0");

        await CreateJob().ImportCkAsync(TenantId, "key-ordinary", null);

        await Assert.That(_imported).IsEquivalentTo(["Customer.System-1.0.0"]);
        await _cacheService.Received(1).DeleteCacheStreamAsync(TenantId, "key-ordinary");
    }

    /// <summary>
    ///     The refusal is final: Hangfire does not retry it. And a job enqueued before this change carries the same
    ///     method signature, so it runs into the same refusal at run time (abuse case: an old enqueued job).
    /// </summary>
    [Test]
    [Arguments(nameof(IImportModelJob.ImportCkAsync))]
    [Arguments(nameof(IImportModelJob.ImportCkBatchAsync))]
    public async Task TheCkImportJobs_DoNotRetryTheRefusal_AndKeepTheirEnqueuedSignature(string methodName)
    {
        var method = typeof(IImportModelJob).GetMethods().Single(m => m.Name == methodName);
        var retry = method.GetCustomAttribute<AutomaticRetryAttribute>();

        await Assert.That(retry).IsNotNull();
        await Assert.That(retry!.ExceptOn).Contains(typeof(ServiceManagedCkModelImportRefusedException));
        await Assert.That(retry.Attempts).IsEqualTo(3);
        var parameters = method.GetParameters().Select(p => p.ParameterType).ToArray();
        await Assert.That(parameters).IsEquivalentTo(methodName == nameof(IImportModelJob.ImportCkAsync)
            ? [typeof(string), typeof(string), typeof(IBotCancellationToken)]
            : new[] { typeof(string), typeof(List<string>), typeof(IBotCancellationToken) });
    }

    private void AddCacheStream(string key, string modelId)
    {
        _cacheService.GetCacheStreamByIdAsync(TenantId, key).Returns(_ => new CacheStream
        {
            Stream = new MemoryStream(Encoding.UTF8.GetBytes($"{modelId}\n{ContentMarker}")),
            ContentType = "application/json",
            FileName = $"{key}.json"
        });
    }

    private ImportModelJob CreateJob()
    {
        var serializer = Substitute.For<ICkSerializer>();
        serializer.DeserializeCompiledModelRootAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<OperationResult>())
            .Returns(async call =>
            {
                using var reader = new StreamReader(call.Arg<Stream>());
                var modelId = (await reader.ReadLineAsync())!;
                return new CkCompiledModelRoot { ModelId = new CkModelId(modelId) };
            });
        var systemContext = Substitute.For<ISystemContext>();
        systemContext.FindTenantContextAsync(TenantId).Returns(_tenantContext);

        var command = new ImportCkModelCommand(_commandLogger, serializer, systemContext);
        return new ImportModelJob(Substitute.For<ILogger<ImportModelJob>>(), systemContext, _cacheService,
            Substitute.For<ICompressionService>(), command, Substitute.For<IImportRtModelCommand>(),
            [TimeSpan.Zero, TimeSpan.Zero]);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception) + (exception == null ? "" : " " + exception.Message)));
        }
    }
}
