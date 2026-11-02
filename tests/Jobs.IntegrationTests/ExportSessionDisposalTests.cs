using System.Diagnostics;
using ExportTestCkModel.Generated.ExportTest.v1;
using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Runtime.Contracts.TransportContainer;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.Jobs.IntegrationTests;

/// <summary>
///     AB#8080 — a failed export (refused stored query, file error) disposes its session, which aborts the transaction
///     it started. Before the fix the transaction stayed open on the server until MongoDB expired it
///     (<c>transactionLifetimeLimitSeconds</c>, default 60 s), and a tenant reset right after it waited that long.
///     Each test asserts on the server itself (<c>$currentOp</c>: no session with an open transaction) and that a
///     reset right after the failed export completes well inside the 60 s lifetime.
/// </summary>
/// <remarks>
///     The integration test classes share one MongoDB and run one after the other (<c>NotInParallel</c>), so an open
///     transaction seen here belongs to this test.
/// </remarks>
[NotInParallel]
public class ExportSessionDisposalTests
{
    private const string DummySecret = "dummy-secret-value-8080";
    private static readonly RtSecurityContext Caller = RtSecurityContext.ForUser("user-m", ["TestManagement"]);

    /// <summary>
    ///     Well below MongoDB's default transaction lifetime (60 s): a reset that has to wait for a leaked transaction
    ///     cannot finish within it, a reset without one takes a few seconds.
    /// </summary>
    private static readonly TimeSpan ResetBudget = TimeSpan.FromSeconds(30);

    private sealed record Seed(ExportEngineFixture Fixture, ITenantRepository Repository, OctoObjectId Continent);

    private static async Task<Seed> SeedAsync()
    {
        var fixture = await ExportEngineFixture.GetAsync();
        await fixture.ResetAsync();
        var repository = await fixture.SystemContext.FindTenantRepositoryAsync(fixture.TenantId);

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var continent = await repository.CreateTransientRtEntityAsync<RtContinent>();
        continent.RtId = OctoObjectId.GenerateNewId();
        continent.Name = "Continent-Europe";
        await repository.InsertOneRtEntityAsync(session, continent);

        var country = await repository.CreateTransientRtEntityAsync<RtCountry>();
        country.RtId = OctoObjectId.GenerateNewId();
        country.Name = "Country-Austria";
        country.SetAttributeValue("Secret", AttributeValueTypesDto.String, DummySecret);
        var result = new OperationResult();
        await repository.ApplyChangesAsync(session,
            new List<IEntityUpdateInfo<RtEntity>> { EntityUpdateInfo<RtEntity>.CreateInsert(country) },
            [
                AssociationUpdateInfo.CreateInsert(
                    new RtEntityId(ExportTestCkIds.RtCkCountryTypeId, country.RtId),
                    new RtEntityId(ExportTestCkIds.RtCkContinentTypeId, continent.RtId),
                    SystemCkIds.RtCkParentChildRoleId)
            ], result);
        await Assert.That(result.HasErrors).IsFalse();
        await session.CommitTransactionAsync();
        return new Seed(fixture, repository, continent.RtId);
    }

    private static async Task<OctoObjectId> StoreQueryAsync(Seed seed, string? hiddenFilterPath)
    {
        using var session = await seed.Repository.GetSessionAsync();
        session.StartTransaction();
        var query = await seed.Repository.CreateTransientRtEntityAsync<RtSimpleRtQuery>();
        query.RtId = OctoObjectId.GenerateNewId();
        query.Name = "Countries";
        query.QueryCkTypeId = ExportTestCkIds.RtCkCountryTypeId.SemanticVersionedFullName;
        query.Columns.Add("Name");
        if (hiddenFilterPath != null)
        {
            query.FieldFilter = new AttributeRecordValueList<RtFieldFilterRecord>
            {
                new() { AttributePath = hiddenFilterPath, Operator = RtFieldFilterOperatorEnum.Equals, ComparisonValue = "guess" }
            };
        }

        await seed.Repository.InsertOneRtEntityAsync(session, query);
        await session.CommitTransactionAsync();
        return query.RtId;
    }

    private static ExportRtModelByQueryByQueryCommand QueryCommand(Seed seed)
    {
        var provider = seed.Fixture.Provider;
        return new ExportRtModelByQueryByQueryCommand(
            NullLogger<ExportRtModelByQueryByQueryCommand>.Instance, seed.Fixture.SystemContext,
            provider.GetRequiredService<ICkCacheService>(), provider.GetRequiredService<IRtEntityToTcDtoConverter>(),
            provider.GetRequiredService<IRtSerializer>());
    }

    private static ExportRtModelByDeepGraphCommand DeepGraphCommand(Seed seed)
    {
        var provider = seed.Fixture.Provider;
        return new ExportRtModelByDeepGraphCommand(
            NullLogger<ExportRtModelByDeepGraphCommand>.Instance, seed.Fixture.SystemContext,
            provider.GetRequiredService<ICkCacheService>(), provider.GetRequiredService<IRtEntityToTcDtoConverter>(),
            provider.GetRequiredService<IRtSerializer>());
    }

    /// <summary>
    ///     A file path whose directory does not exist: the export reads everything inside its transaction and fails
    ///     when it opens the file, before the commit.
    /// </summary>
    private static string UnwritablePath() =>
        Path.Combine(Path.GetTempPath(), $"ab8080-missing-{Guid.NewGuid():N}", "export.json");

    /// <summary>Sessions on the server that hold an open transaction, idle ones included.</summary>
    private static async Task<long> CountOpenTransactionsAsync(ExportEngineFixture fixture)
    {
        var configuration = fixture.Provider.GetRequiredService<IOptions<OctoSystemConfiguration>>().Value;
        var settings = new MongoClientSettings
        {
            Server = MongoServerAddress.Parse(configuration.DatabaseHost),
            Credential = MongoCredential.CreateCredential("admin", configuration.AdminUser,
                configuration.AdminUserPassword),
            DirectConnection = true
        };
        using var client = new MongoClient(settings);
        var pipeline = new[]
        {
            new BsonDocument("$currentOp", new BsonDocument { { "allUsers", true }, { "idleSessions", true } }),
            new BsonDocument("$match", new BsonDocument("transaction", new BsonDocument("$exists", true)))
        };
        var cursor = await client.GetDatabase("admin").AggregateAsync<BsonDocument>(pipeline);
        return (await cursor.ToListAsync()).Count;
    }

    private static async Task AssertNoOpenTransactionAndAQuickResetAsync(ExportEngineFixture fixture)
    {
        await Assert.That(await CountOpenTransactionsAsync(fixture)).IsEqualTo(0);

        var watch = Stopwatch.StartNew();
        await fixture.ResetAsync();
        watch.Stop();
        await Assert.That(watch.Elapsed).IsLessThan(ResetBudget);
    }

    [Test]
    public async Task ARefusedQueryExport_LeavesNoOpenTransaction_AndAResetRightAfterItDoesNotWait()
    {
        var seed = await SeedAsync();
        var query = await StoreQueryAsync(seed, "Secret");
        var command = QueryCommand(seed);

        // Twice, as in the bug report: every refused run used to leave one more open transaction behind.
        for (var i = 0; i < 2; i++)
        {
            var file = Path.GetTempFileName();
            try
            {
                var failure = await Assert.That(async () =>
                        await command.ExportAsync(seed.Fixture.TenantId, query, Caller, file, null))
                    .Throws<CommandExecutionFailedException>();
                await Assert.That(failure!.Message).Contains("ATTRIBUTE_NOT_QUERYABLE");
            }
            finally
            {
                File.Delete(file);
            }
        }

        await AssertNoOpenTransactionAndAQuickResetAsync(seed.Fixture);
    }

    [Test]
    public async Task AQueryExportThatFailsAfterItsReads_LeavesNoOpenTransaction()
    {
        var seed = await SeedAsync();
        var query = await StoreQueryAsync(seed, null);

        await Assert.That(async () =>
                await QueryCommand(seed).ExportAsync(seed.Fixture.TenantId, query, Caller, UnwritablePath(), null))
            .Throws<DirectoryNotFoundException>();

        await AssertNoOpenTransactionAndAQuickResetAsync(seed.Fixture);
    }

    [Test]
    public async Task ADeepGraphExportThatFailsAfterItsReads_LeavesNoOpenTransaction()
    {
        var seed = await SeedAsync();
        var request = new ExportRtByDeepGraphCommandRequest(seed.Fixture.TenantId, [seed.Continent],
            ExportTestCkIds.RtCkContinentTypeId);

        await Assert.That(async () =>
                await DeepGraphCommand(seed).ExportAsync(seed.Fixture.TenantId, request, Caller, UnwritablePath(), null))
            .Throws<DirectoryNotFoundException>();

        await AssertNoOpenTransactionAndAQuickResetAsync(seed.Fixture);
    }

    [Test]
    public async Task ASuccessfulExport_StillExports_AndLeavesNoOpenTransaction()
    {
        var seed = await SeedAsync();
        var query = await StoreQueryAsync(seed, null);
        var file = Path.GetTempFileName();
        try
        {
            await QueryCommand(seed).ExportAsync(seed.Fixture.TenantId, query, Caller, file, null);
            await Assert.That(await File.ReadAllTextAsync(file)).Contains("Country-Austria");
        }
        finally
        {
            File.Delete(file);
        }

        await AssertNoOpenTransactionAndAQuickResetAsync(seed.Fixture);
    }
}
