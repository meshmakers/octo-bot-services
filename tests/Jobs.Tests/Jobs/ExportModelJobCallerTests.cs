using Meshmakers.Common.Shared.Services;
using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs;

/// <summary>
///     AB#6734 — the export job builds the security context from the request and hands it to the command; a job
///     without caller context (enqueued by an older asset repository, or by an old consumer before the update)
///     fails with a clear message and produces no file - it is never exported unfiltered.
/// </summary>
public class ExportModelJobCallerTests
{
    private const string TenantId = "tenant-1";

    private readonly IExportRtModelByQueryCommand _queryCommand = Substitute.For<IExportRtModelByQueryCommand>();
    private readonly IExportRtModelByDeepGraphCommand _deepGraphCommand =
        Substitute.For<IExportRtModelByDeepGraphCommand>();
    private readonly IDistributedCacheService _cache = Substitute.For<IDistributedCacheService>();

    private ExportModelJob CreateJob() =>
        new(Substitute.For<ILogger<ExportModelJob>>(), _cache, Substitute.For<ICompressionService>(), _queryCommand,
            _deepGraphCommand);

    [Test]
    public async Task Query_HandsTheCallerContextToTheCommand()
    {
        var queryId = OctoObjectId.GenerateNewId();
        var request = new ExportRtByQueryCommandRequest(TenantId, queryId)
        {
            InitiatedBySubjectId = "user-1",
            InitiatedByRoleNames = ["Reader"],
            InitiatedByClientId = "studio"
        };

        await CreateJob().ExportRtModelByQueryAsync(request, null);

        await _queryCommand.Received(1).ExportAsync(TenantId, queryId,
            Arg.Is<RtSecurityContext>(c => !c.IsSystem && c.SubjectId == "user-1" && c.Roles.SequenceEqual(new[] { "Reader" })),
            Arg.Any<string>(), Arg.Any<CancellationToken?>());
    }

    [Test]
    public async Task DeepGraph_HandsTheCallerContextToTheCommand()
    {
        var request = new ExportRtByDeepGraphCommandRequest(TenantId, [OctoObjectId.GenerateNewId()],
            new RtCkId<CkTypeId>("Test/Customer"))
        {
            InitiatedBySubjectId = "svc-client",
            InitiatedByRoleNames = []
        };

        await CreateJob().ExportRtModelByDeepGraphAsync(request, null);

        await _deepGraphCommand.Received(1).ExportAsync(TenantId, request,
            Arg.Is<RtSecurityContext>(c => !c.IsSystem && c.SubjectId == "svc-client" && c.Roles.Count == 0),
            Arg.Any<string>(), Arg.Any<CancellationToken?>());
    }

    [Test]
    public async Task OldJobWithoutCallerContext_FailsClearly_AndExportsNothing()
    {
        var oldQuery = new ExportRtByQueryCommandRequest(TenantId, OctoObjectId.GenerateNewId());
        var oldDeepGraph = new ExportRtByDeepGraphCommandRequest(TenantId, [OctoObjectId.GenerateNewId()],
            new RtCkId<CkTypeId>("Test/Customer"));

        var byQuery = await Assert.That(async () => await CreateJob().ExportRtModelByQueryAsync(oldQuery, null))
            .Throws<JobFailedException>();
        var byDeepGraph = await Assert
            .That(async () => await CreateJob().ExportRtModelByDeepGraphAsync(oldDeepGraph, null))
            .Throws<JobFailedException>();

        await Assert.That(byQuery!.Message).Contains("no caller identity");
        await Assert.That(byDeepGraph!.Message).Contains("older asset repository");
        await _queryCommand.DidNotReceiveWithAnyArgs().ExportAsync(default!, default, default!, default!, default);
        await _deepGraphCommand.DidNotReceiveWithAnyArgs().ExportAsync(default!, default!, default!, default!, default);
        await _cache.DidNotReceiveWithAnyArgs().CreateStreamAsync(default!, default!, default!, default!, default);
    }

    [Test]
    public async Task BlankSubject_IsTreatedLikeNoCaller()
    {
        var request = new ExportRtByQueryCommandRequest(TenantId, OctoObjectId.GenerateNewId())
        {
            InitiatedBySubjectId = "  ",
            InitiatedByRoleNames = ["Reader"]
        };

        await Assert.That(async () => await CreateJob().ExportRtModelByQueryAsync(request, null))
            .Throws<JobFailedException>();
    }
}
