using ExportTestCkModel.Generated.ExportTest.v1;
using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Runtime.Contracts.TransportContainer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meshmakers.Octo.Backend.Jobs.IntegrationTests;

/// <summary>
///     AB#8049 — the query export against the real engine and MongoDB: a stored query whose own field filter or sort
///     order reaches a CK v2 Hidden attribute (<c>ExportTest/Country.Secret</c>, standing in for
///     <c>System.Identity/User.PasswordHash</c>) is refused before it runs. Without the check the repository applies
///     the filter and the number of exported rows answers "does the stored value equal / start with my guess". The
///     stored value is a fixed dummy constant.
/// </summary>
[NotInParallel]
public class StoredQueryHiddenAttributeExportTests
{
    private const string DummySecret = "dummy-secret-value-8049";
    private static readonly RtSecurityContext Caller = RtSecurityContext.ForUser("user-m", ["TestManagement"]);

    private sealed record Seed(ExportEngineFixture Fixture, ITenantRepository Repository);

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

        // The identity service writes such a value through the repository; simulate that write here.
        var country = await repository.CreateTransientRtEntityAsync<RtCountry>();
        country.RtId = OctoObjectId.GenerateNewId();
        country.Name = "Country-Austria";
        country.SetAttributeValue("Secret", AttributeValueTypesDto.String, DummySecret);
        await repository.InsertOneRtEntityAsync(session, country);
        await session.CommitTransactionAsync();
        return new Seed(fixture, repository);
    }

    private static async Task<OctoObjectId> StoreQueryAsync(Seed seed, string? filterPath, RtFieldFilterOperatorEnum op,
        string? comparison, string? sortPath)
    {
        using var session = await seed.Repository.GetSessionAsync();
        session.StartTransaction();
        var query = await seed.Repository.CreateTransientRtEntityAsync<RtSimpleRtQuery>();
        query.RtId = OctoObjectId.GenerateNewId();
        query.Name = "Countries";
        query.QueryCkTypeId = ExportTestCkIds.RtCkCountryTypeId.SemanticVersionedFullName;
        query.Columns.Add("Name");
        if (filterPath != null)
        {
            query.FieldFilter = new AttributeRecordValueList<RtFieldFilterRecord>
            {
                new() { AttributePath = filterPath, Operator = op, ComparisonValue = comparison }
            };
        }

        if (sortPath != null)
        {
            query.Sorting = new AttributeRecordValueList<RtSortOrderItemRecord>
            {
                new() { AttributePath = sortPath, SortOrder = RtSortOrdersEnum.Ascending }
            };
        }

        // Stored directly through the repository, as an import or a restore would - the asset repository's store-time
        // check (AB#8049) does not run on this path, so the job must not trust the stored query.
        await seed.Repository.InsertOneRtEntityAsync(session, query);
        await session.CommitTransactionAsync();
        return query.RtId;
    }

    private static async Task<string> ExportAsync(Seed seed, OctoObjectId queryId)
    {
        var provider = seed.Fixture.Provider;
        var command = new ExportRtModelByQueryByQueryCommand(
            NullLogger<ExportRtModelByQueryByQueryCommand>.Instance, seed.Fixture.SystemContext,
            provider.GetRequiredService<ICkCacheService>(), provider.GetRequiredService<IRtEntityToTcDtoConverter>(),
            provider.GetRequiredService<IRtSerializer>());
        var file = Path.GetTempFileName();
        try
        {
            await command.ExportAsync(seed.Fixture.TenantId, queryId, Caller, file, null);
            return await File.ReadAllTextAsync(file);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    [Arguments("Secret", RtFieldFilterOperatorEnum.Equals, DummySecret)]
    [Arguments("secret", RtFieldFilterOperatorEnum.Like, "dummy%")]
    [Arguments("Secret", RtFieldFilterOperatorEnum.MatchRegEx, "^dummy")]
    [Arguments("Secret", RtFieldFilterOperatorEnum.GreaterThan, "d")]
    public async Task AStoredFilterOnAHiddenAttribute_IsRefused_ForTheRightAndAWrongGuessAlike(string path,
        RtFieldFilterOperatorEnum op, string? comparison)
    {
        var seed = await SeedAsync();
        var right = await StoreQueryAsync(seed, path, op, comparison, null);
        var wrong = await StoreQueryAsync(seed, path, op, "wrong-guess", null);

        var rightFailure = await Assert.That(async () => await ExportAsync(seed, right))
            .Throws<CommandExecutionFailedException>();
        await Assert.That(async () => await ExportAsync(seed, wrong)).Throws<CommandExecutionFailedException>();

        await Assert.That(rightFailure!.Message).Contains("ATTRIBUTE_NOT_QUERYABLE").And.Contains("field filter");
        await Assert.That(rightFailure.Message).DoesNotContain(DummySecret);
    }

    [Test]
    public async Task AStoredSortOnAHiddenAttribute_IsRefused()
    {
        var seed = await SeedAsync();
        var query = await StoreQueryAsync(seed, null, RtFieldFilterOperatorEnum.Equals, null, "Secret");

        var failure = await Assert.That(async () => await ExportAsync(seed, query))
            .Throws<CommandExecutionFailedException>();

        await Assert.That(failure!.Message).Contains("sort order");
    }

    [Test]
    public async Task AStoredQueryOnVisibleAttributes_StillExports_WithoutTheHiddenValue()
    {
        var seed = await SeedAsync();
        var query = await StoreQueryAsync(seed, "Name", RtFieldFilterOperatorEnum.Equals, "Country-Austria", "Name");

        var exported = await ExportAsync(seed, query);

        await Assert.That(exported).Contains("Country-Austria");
        await Assert.That(exported).DoesNotContain(DummySecret);
    }
}
