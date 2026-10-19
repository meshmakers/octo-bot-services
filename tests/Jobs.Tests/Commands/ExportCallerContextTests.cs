using Meshmakers.Octo.Backend.Jobs;
using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Runtime.Contracts.TransportContainer;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Commands;

/// <summary>
///     AB#6734 — the export commands open their repository session for the requesting caller and never for the
///     system: the session comes from <c>ISecureSessionFactory.GetSessionAsync(RtSecurityContext)</c> with the
///     caller's subject and roles; the system context, an empty caller and a repository that cannot open a
///     caller-scoped session (the extension method would fall back to a system session) are refused before any
///     session is opened or any row is read.
/// </summary>
public class ExportCallerContextTests
{
    private const string TenantId = "tenant-1";

    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();
    private readonly ICkCacheService _ckCacheService = Substitute.For<ICkCacheService>();
    private readonly IRtEntityToTcDtoConverter _converter = Substitute.For<IRtEntityToTcDtoConverter>();
    private readonly IRtSerializer _serializer = Substitute.For<IRtSerializer>();

    private static readonly RtSecurityContext Caller = RtSecurityContext.ForUser("user-1", ["Reader", "Analyst"]);

    private ExportRtModelByQueryByQueryCommand QueryCommand() =>
        new(Substitute.For<ILogger<ExportRtModelByQueryByQueryCommand>>(), _systemContext, _ckCacheService,
            _converter, _serializer);

    private ExportRtModelByDeepGraphCommand DeepGraphCommand() =>
        new(Substitute.For<ILogger<ExportRtModelByDeepGraphCommand>>(), _systemContext, _ckCacheService,
            _converter, _serializer);

    private static ExportRtByDeepGraphCommandRequest DeepGraphRequest() =>
        new(TenantId, [OctoObjectId.GenerateNewId()], new RtCkId<CkTypeId>("Test/Customer"));

    /// <summary>A repository that can open caller-scoped sessions, as the real tenant repository can.</summary>
    private (ITenantRepository Repository, ISecureSessionFactory Factory, IOctoSession Session) SecureRepository()
    {
        var repository = Substitute.For<ITenantRepository, ISecureSessionFactory>();
        var factory = (ISecureSessionFactory)repository;
        var session = Substitute.For<IOctoSession>();
        factory.GetSessionAsync(Arg.Any<RtSecurityContext>()).Returns(session);
        _systemContext.FindTenantRepositoryAsync(TenantId).Returns(repository);
        return (repository, factory, session);
    }

    [Test]
    public async Task Query_OpensTheSessionForTheCaller_NotTheSystemSession()
    {
        var (repository, factory, _) = SecureRepository();

        // The query does not exist: the export stops right after the session was opened.
        await Assert.That(async () => await QueryCommand().ExportAsync(TenantId, OctoObjectId.GenerateNewId(), Caller,
            Path.GetTempFileName(), null)).Throws<Exception>();

        await factory.Received(1).GetSessionAsync(Arg.Is<RtSecurityContext>(c =>
            !c.IsSystem && c.SubjectId == "user-1" && c.Roles.Contains("Reader") && c.Roles.Contains("Analyst")));
        await repository.DidNotReceive().GetSessionAsync();
    }

    [Test]
    public async Task DeepGraph_OpensTheSessionForTheCaller_NotTheSystemSession()
    {
        var (repository, factory, _) = SecureRepository();

        // The substituted repository answers the deep graph with an empty result: the export completes.
        await DeepGraphCommand().ExportAsync(TenantId, DeepGraphRequest(), Caller, Path.GetTempFileName(), null);

        await factory.Received(1).GetSessionAsync(Arg.Is<RtSecurityContext>(c =>
            !c.IsSystem && c.SubjectId == "user-1" && c.Roles.Contains("Reader") && c.Roles.Contains("Analyst")));
        await repository.DidNotReceive().GetSessionAsync();
    }

    [Test]
    public async Task SystemContext_IsRefused_BeforeAnySessionIsOpened()
    {
        var (repository, factory, _) = SecureRepository();

        await Assert.That(async () => await QueryCommand().ExportAsync(TenantId, OctoObjectId.GenerateNewId(),
            RtSecurityContext.System, Path.GetTempFileName(), null)).Throws<JobFailedException>();
        await Assert.That(async () => await DeepGraphCommand().ExportAsync(TenantId, DeepGraphRequest(),
            RtSecurityContext.System, Path.GetTempFileName(), null)).Throws<JobFailedException>();

        await factory.DidNotReceive().GetSessionAsync(Arg.Any<RtSecurityContext>());
        await repository.DidNotReceive().GetSessionAsync();
    }

    [Test]
    public async Task CallerWithoutSubject_IsRefused()
    {
        var (repository, factory, _) = SecureRepository();
        var noSubject = RtSecurityContext.ForUser(null, ["Reader"]);

        await Assert.That(async () => await QueryCommand().ExportAsync(TenantId, OctoObjectId.GenerateNewId(),
            noSubject, Path.GetTempFileName(), null)).Throws<JobFailedException>();
        await Assert.That(async () => await DeepGraphCommand().ExportAsync(TenantId, DeepGraphRequest(),
            noSubject, Path.GetTempFileName(), null)).Throws<JobFailedException>();

        await factory.DidNotReceive().GetSessionAsync(Arg.Any<RtSecurityContext>());
        await repository.DidNotReceive().GetSessionAsync();
    }

    [Test]
    public async Task RepositoryWithoutCallerScopedSessions_IsRefused_NoFallbackToTheSystemSession()
    {
        // A plain ITenantRepository: the GetSessionAsync(RtSecurityContext) extension would silently hand out
        // the system session here, which is exactly the hole this story closes.
        var repository = Substitute.For<ITenantRepository>();
        _systemContext.FindTenantRepositoryAsync(TenantId).Returns(repository);

        await Assert.That(async () => await QueryCommand().ExportAsync(TenantId, OctoObjectId.GenerateNewId(), Caller,
            Path.GetTempFileName(), null)).Throws<JobFailedException>();
        await Assert.That(async () => await DeepGraphCommand().ExportAsync(TenantId, DeepGraphRequest(), Caller,
            Path.GetTempFileName(), null)).Throws<JobFailedException>();

        await repository.DidNotReceive().GetSessionAsync();
    }
}
