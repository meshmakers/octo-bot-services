using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Runtime.Contracts.TransportContainer;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Commands;

/// <summary>
///     AB#8049 — the query export (<c>ExportRtByQuery</c>) refuses a stored query whose own field filter or sort order
///     reaches a CK v2 Hidden attribute before the query runs: the set of exported rows would answer a guess of the
///     hidden value (password-hash oracle). Synthetic CK graph modelled on <c>System.Identity/User</c>: type
///     <c>Test/Account</c> with Name, PasswordHash (Hidden) and the record array Credentials whose member Token is Hidden;
///     <c>Test/Plain</c> has only Name. Dummy values only.
/// </summary>
public class StoredQueryAccessGuardTests
{
    private const string TenantId = "tenant-1";
    private const string Model = "Test-1.0.0";
    private const string Guess = "dummy-guess-0001";

    private static readonly RtCkId<CkTypeId> AccountType = new("Test/Account");
    private static readonly RtCkId<CkTypeId> PlainType = new("Test/Plain");
    private static readonly RtSecurityContext Caller = RtSecurityContext.ForUser("user-1", ["Reader"]);

    private readonly ICkCacheService _cache = Cache();
    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();

    // ------------------------------------------------------------------------------------------- the rule itself

    [Test]
    [Arguments("PasswordHash")]
    [Arguments("passwordHash")]
    [Arguments("Credentials.Token")]
    [Arguments("credentials")]
    [Arguments("members.Test/Account[passwordHash=dummy-guess-0001]->name")]
    [Arguments("parent.association->passwordHash")]
    public async Task AHiddenPath_IsRefused_WithoutTheValue(string path)
    {
        var exception = await Assert.That(() => StoredQueryAccessGuard.EnsureNotHidden(_cache, TenantId,
                OctoObjectId.GenerateNewId(), AccountType, [(path, StoredQueryAccessGuard.FieldFilterUsage)]))
            .Throws<CommandExecutionFailedException>();

        await Assert.That(exception!.Message).Contains("ATTRIBUTE_NOT_QUERYABLE");
        await Assert.That(exception.Message).DoesNotContain(Guess);
    }

    [Test]
    [Arguments("Name")]
    [Arguments("credentials.label")]
    [Arguments("members.Test/Account[name=x]->name")]
    [Arguments("parent.association::totalCount")]
    public async Task AVisiblePath_Passes(string path)
    {
        await Assert.That(() => StoredQueryAccessGuard.EnsureNotHidden(_cache, TenantId, OctoObjectId.GenerateNewId(),
            AccountType, [(path, StoredQueryAccessGuard.FieldFilterUsage)])).ThrowsNothing();
    }

    [Test]
    public async Task AQueryTypeTheCacheDoesNotKnow_IsCheckedAgainstEveryType()
    {
        await Assert.That(() => StoredQueryAccessGuard.EnsureNotHidden(_cache, TenantId, OctoObjectId.GenerateNewId(),
                new RtCkId<CkTypeId>("Test/Named"), [("PasswordHash", StoredQueryAccessGuard.SortOrderUsage)]))
            .Throws<CommandExecutionFailedException>();
    }

    // ------------------------------------------------------------------------------------ the export command

    [Test]
    public async Task Export_AStoredFilterOnAHiddenAttribute_FailsBeforeTheQueryRuns()
    {
        var repository = RepositoryReturning(new RtSimpleRtQuery
        {
            QueryCkTypeId = AccountType.ToString(),
            Columns = new AttributeStringValueList(["Name"]),
            FieldFilter = new AttributeRecordValueList<RtFieldFilterRecord>
            {
                new() { AttributePath = "passwordHash", Operator = RtFieldFilterOperatorEnum.Like, ComparisonValue = Guess }
            }
        });

        var exception = await Assert.That(async () => await ExportAsync()).Throws<CommandExecutionFailedException>();

        await Assert.That(exception!.Message).Contains("field filter");
        await Assert.That(exception.Message).DoesNotContain(Guess);
        await repository.DidNotReceiveWithAnyArgs()
            .GetRtEntitiesByTypeAsync(default!, default(RtCkId<CkTypeId>)!, default(RtEntityQueryOptions)!);
    }

    [Test]
    public async Task Export_AStoredSortOnAHiddenAttribute_FailsBeforeTheQueryRuns()
    {
        var repository = RepositoryReturning(new RtSimpleRtQuery
        {
            QueryCkTypeId = AccountType.ToString(),
            Columns = new AttributeStringValueList(["Name"]),
            Sorting = new AttributeRecordValueList<RtSortOrderItemRecord>
            {
                new() { AttributePath = "PasswordHash", SortOrder = RtSortOrdersEnum.Ascending }
            }
        });

        var exception = await Assert.That(async () => await ExportAsync()).Throws<CommandExecutionFailedException>();

        await Assert.That(exception!.Message).Contains("sort order");
        await repository.DidNotReceiveWithAnyArgs()
            .GetRtEntitiesByTypeAsync(default!, default(RtCkId<CkTypeId>)!, default(RtEntityQueryOptions)!);
    }

    [Test]
    public async Task Export_AStoredQueryOnVisibleAttributes_Runs()
    {
        var repository = RepositoryReturning(new RtSimpleRtQuery
        {
            QueryCkTypeId = AccountType.ToString(),
            Columns = new AttributeStringValueList(["Name"]),
            FieldFilter = new AttributeRecordValueList<RtFieldFilterRecord>
            {
                new() { AttributePath = "Name", Operator = RtFieldFilterOperatorEnum.Equals, ComparisonValue = "x" }
            }
        });

        await ExportAsync();

        await repository.ReceivedWithAnyArgs(1)
            .GetRtEntitiesByTypeAsync(default!, default(RtCkId<CkTypeId>)!, default(RtEntityQueryOptions)!);
    }

    // ------------------------------------------------------------------------------------------------- helpers

    /// <summary>Runs the export into a temp file and always deletes the file.</summary>
    private async Task ExportAsync()
    {
        var file = Path.GetTempFileName();
        try
        {
            await Command().ExportAsync(TenantId, OctoObjectId.GenerateNewId(), Caller, file, null);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private ExportRtModelByQueryByQueryCommand Command() =>
        new(Substitute.For<ILogger<ExportRtModelByQueryByQueryCommand>>(), _systemContext, _cache,
            Substitute.For<IRtEntityToTcDtoConverter>(), Substitute.For<IRtSerializer>());

    private ITenantRepository RepositoryReturning(RtEntity query)
    {
        var repository = Substitute.For<ITenantRepository, ISecureSessionFactory>();
        ((ISecureSessionFactory)repository).GetSessionAsync(Arg.Any<RtSecurityContext>())
            .Returns(Substitute.For<IOctoSession>());
        repository.GetRtEntityByRtIdAsync(Arg.Any<IOctoSession>(), Arg.Any<RtEntityId>()).Returns(query);
        var empty = Substitute.For<IResultSet<RtEntity>>();
        empty.Items.Returns(new List<RtEntity>());
        repository.GetRtEntitiesByTypeAsync(Arg.Any<IOctoSession>(), Arg.Any<RtCkId<CkTypeId>>(),
            Arg.Any<RtEntityQueryOptions>()).ReturnsForAnyArgs(empty);
        _systemContext.FindTenantRepositoryAsync(TenantId).Returns(repository);
        return repository;
    }

    private static CkTypeAttributeGraph Attr(string name, CkAttributeAccessDto? access,
        AttributeValueTypesDto valueType = AttributeValueTypesDto.String, CkId<CkRecordId>? record = null)
    {
        var attrId = new CkId<CkAttributeId>($"{Model}/{name}");
        var definition = new CkAttributeGraph(attrId,
            new CkAttributeDto { AttributeId = name, ValueType = valueType, ValueCkRecordId = record });
        return new CkTypeAttributeGraph(attrId,
            new CkTypeAttributeDto { CkAttributeId = attrId, AttributeName = name, IsOptional = true, Access = access },
            definition);
    }

    private static CkTypeGraph Type(string name, params CkTypeAttributeGraph[] attrs) => new(
        new CkId<CkTypeId>($"{Model}/{name}"), isAbstract: false, isFinal: false, isCollectionRoot: false,
        baseTypes: [], derivedFromCkTypeId: null, definingCollectionRootCkTypeId: null, derivedTypes: [],
        definedAttributes: [], allAttributes: attrs.ToDictionary(a => a.CkAttributeId, a => a), indexes: [],
        associations: new CkGraphDirectedAssociations([]), description: "test",
        enableChangeStreamPreAndPostImages: false, allMethods: null);

    private static ICkCacheService Cache()
    {
        var recordId = new CkId<CkRecordId>($"{Model}/Credential");
        var credentialAttrs = new[] { Attr("Label", null), Attr("Token", CkAttributeAccessDto.Hidden) };
        var credential = new CkRecordGraph(recordId, isAbstract: false, isFinal: false, baseRecords: [],
            derivedFromCkRecordId: null, derivedRecords: [], definedAttributes: [],
            allAttributes: credentialAttrs.ToDictionary(a => a.CkAttributeId, a => a), description: "test");
        var account = Type("Account", Attr("Name", null), Attr("PasswordHash", CkAttributeAccessDto.Hidden),
            Attr("Credentials", null, AttributeValueTypesDto.RecordArray, recordId));
        var plain = Type("Plain", Attr("Name", null));

        var cache = Substitute.For<ICkCacheService>();
        cache.IsTenantLoaded(TenantId).Returns(true);
        cache.GetRtCkType(TenantId, AccountType).Returns(account);
        cache.GetRtCkType(TenantId, PlainType).Returns(plain);
        cache.TryGetRtCkType(TenantId, AccountType, out Arg.Any<CkTypeGraph?>())
            .Returns(call => { call[2] = account; return true; });
        cache.TryGetRtCkType(TenantId, PlainType, out Arg.Any<CkTypeGraph?>())
            .Returns(call => { call[2] = plain; return true; });
        cache.GetCkTypes(TenantId).Returns(new List<CkTypeGraph> { account, plain });
        cache.GetRtCkRecord(TenantId, Arg.Any<RtCkId<CkRecordId>>()).Returns(credential);
        return cache;
    }
}
