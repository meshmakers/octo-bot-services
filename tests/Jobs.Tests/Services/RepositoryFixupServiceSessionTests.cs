using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Bot.Generated.System.Bot.v3;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RepositoryUpdate.Services;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Services;

/// <summary>
///     AB#8080 — the fixup job disposes the session of every script run, also when the script fails. Disposing the
///     engine session aborts a transaction that was not committed; without it the write transaction stayed open until
///     MongoDB expired it (default 60 s) and blocked DDL on the tenant database.
/// </summary>
public class RepositoryFixupServiceSessionTests
{
    private const string TenantId = "fixuptenant";

    private sealed record Setup(RepositoryFixupService Service, IOctoSession QuerySession, IOctoSession ScriptSession,
        IRepositoryOpsService Ops, ITenantRepository Repository);

    private static Setup Create()
    {
        var querySession = Substitute.For<IOctoSession>();
        var scriptSession = Substitute.For<IOctoSession>();

        var fixup = new RtFixup
        {
            RtId = OctoObjectId.GenerateNewId(),
            Enabled = true,
            IsApplied = false,
            Order = 1,
            Script = "db.example.find()"
        };
        var resultSet = Substitute.For<IResultSet<RtFixup>>();
        resultSet.TotalCount.Returns(1);
        resultSet.Items.Returns([fixup]);

        var repository = Substitute.For<ITenantRepository>();
        repository.GetSessionAsync().Returns(querySession, scriptSession);
        repository.GetRtEntitiesByTypeAsync<RtFixup>(querySession, Arg.Any<RtEntityQueryOptions>())
            .Returns(resultSet);

        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.GetTenantRepository().Returns(repository);
        tenantContext.DatabaseName.Returns(TenantId);

        var systemContext = Substitute.For<ISystemContext>();
        systemContext.FindTenantContextAsync(TenantId).Returns(tenantContext);

        var ops = Substitute.For<IRepositoryOpsService>();
        var service = new RepositoryFixupService(NullLogger<RepositoryFixupService>.Instance, systemContext, ops);
        return new Setup(service, querySession, scriptSession, ops, repository);
    }

    [Test]
    public async Task AFailingScript_DisposesItsSession_WithoutCommitting()
    {
        var setup = Create();
        setup.Ops.ExecuteMongoShellScriptAsync(TenantId, Arg.Any<string>(), Arg.Any<CancellationToken?>())
            .ThrowsAsync(new InvalidOperationException("mongosh failed"));

        await Assert.That(async () => await setup.Service.FixupRepositoryAsync(TenantId))
            .Throws<Exception>();

        setup.ScriptSession.Received(1).StartTransaction();
        await setup.ScriptSession.DidNotReceive().CommitTransactionAsync();
        setup.ScriptSession.Received(1).Dispose();
        setup.QuerySession.Received(1).Dispose();
    }

    [Test]
    public async Task ASuccessfulScript_CommitsAndDisposesItsSession()
    {
        var setup = Create();
        setup.Ops.ExecuteMongoShellScriptAsync(TenantId, Arg.Any<string>(), Arg.Any<CancellationToken?>())
            .Returns(new CommandResult { Success = true, Output = "ok" });

        await setup.Service.FixupRepositoryAsync(TenantId);

        await setup.ScriptSession.Received(1).CommitTransactionAsync();
        setup.ScriptSession.Received(1).Dispose();
        setup.QuerySession.Received(1).Dispose();
    }
}
