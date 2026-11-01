using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Runtime.Contracts.TransportContainer;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.IntegrationTests;

/// <summary>
///     AB#7781 — credential guard regression tests of the two runtime export jobs with the real <c>System.Identity</c> model
///     (<see cref="SystemIdentitySnapshotCkModel" />): a user whose password hash, security stamp and two-factor token hold
///     fixed dummy values (never real credentials) is exported by stored query and by deep graph. The exported file, every
///     error and every log line written during the test (Debug and above, the MongoDB command log included) must not contain
///     a dummy value, and a stored query whose filter or sort order reaches one of the Hidden credential attributes is refused
///     before it runs (AB#8049: the set of exported rows would answer a guess of the value).
/// </summary>
/// <remarks>
///     The commands are the very classes the Hangfire jobs run; nothing is mocked below them. The identity service writes the
///     credential attributes through the repository; the seed does the same. One shared tenant: <c>NotInParallel</c>.
/// </remarks>
[NotInParallel]
public class IdentityCredentialGuardExportTests
{
    private const string DummyPasswordHash = "DUMMY-HASH-7781";
    private const string DummySecurityStamp = "DUMMY-STAMP-7781";
    private const string DummyTokenValue = "DUMMY-TOKEN-7781";
    private static readonly string[] DummyValues = [DummyPasswordHash, DummySecurityStamp, DummyTokenValue];

    private const string UserCkTypeId = "System.Identity/User";
    private const string RoleCkTypeId = "System.Identity/Role";
    private const string ProbeUserName = "probe-user-7781";

    private static readonly RtSecurityContext Caller = RtSecurityContext.ForUser("user-admin", ["UserManagement"]);

    private sealed record Seed(ExportEngineFixture Fixture, ITenantRepository Repository, OctoObjectId User,
        OctoObjectId Role, int LogStart, int LogEnd, int Generation);

    private static readonly SemaphoreSlim SeedGate = new(1, 1);
    private static Seed? _seed;

    /// <summary>
    ///     The tenant with System.Identity and the probe user, seeded once for the class (re-seeded when another class reset the
    ///     tenant in between). Seeding once matters for the run time: a refused export leaves its repository session open (the
    ///     command does not dispose it, AB#8080), and a reset (drop database) then waits until MongoDB expires that transaction.
    /// </summary>
    private static async Task<Seed> SeedAsync()
    {
        var fixture = await ExportEngineFixture.GetAsync();
        await SeedGate.WaitAsync();
        try
        {
            if (_seed == null || _seed.Generation != fixture.ResetGeneration)
            {
                _seed = await CreateSeedAsync(fixture);
            }

            return _seed;
        }
        finally
        {
            SeedGate.Release();
        }
    }

    private static async Task<Seed> CreateSeedAsync(ExportEngineFixture fixture)
    {
        await fixture.ResetAsync();
        await fixture.ImportSystemIdentityAsync();
        fixture.Logs.Enabled = true;
        var logStart = fixture.Logs.Count;
        var repository = await fixture.SystemContext.FindTenantRepositoryAsync(fixture.TenantId);

        var userCkTypeId = new RtCkId<CkTypeId>(UserCkTypeId);
        var user = await repository.CreateTransientRtEntityByRtCkIdAsync(userCkTypeId);
        user.SetAttributeValue("UserName", AttributeValueTypesDto.String, ProbeUserName);
        user.SetAttributeValue("NormalizedUserName", AttributeValueTypesDto.String, ProbeUserName.ToUpperInvariant());
        user.SetAttributeValue("Email", AttributeValueTypesDto.String, "probe-user-7781@example.invalid");
        user.SetAttributeValue("NormalizedEmail", AttributeValueTypesDto.String, "PROBE-USER-7781@EXAMPLE.INVALID");
        user.SetAttributeValue("EmailConfirmed", AttributeValueTypesDto.Boolean, true);
        user.SetAttributeValue("PhoneNumberConfirmed", AttributeValueTypesDto.Boolean, false);
        user.SetAttributeValue("TwoFactorEnabled", AttributeValueTypesDto.Boolean, true);
        user.SetAttributeValue("LockoutEnabled", AttributeValueTypesDto.Boolean, true);
        user.SetAttributeValue("AccessFailedCount", AttributeValueTypesDto.Integer, 0);
        user.SetAttributeValue("ResetPasswordOnLogin", AttributeValueTypesDto.Boolean, false);
        user.SetAttributeValue("PasswordHash", AttributeValueTypesDto.String, DummyPasswordHash);
        user.SetAttributeValue("SecurityStamp", AttributeValueTypesDto.String, DummySecurityStamp);
        user.SetAttributeValue("UserTokens", AttributeValueTypesDto.RecordArray, new List<RtRecord>
        {
            new(new RtCkId<CkRecordId>("System.Identity/UserToken"), new Dictionary<string, object?>
            {
                ["LoginProvider"] = "[AspNetUserStore]",
                ["Name"] = "AuthenticatorKey",
                ["UserId"] = ProbeUserName,
                ["Value"] = DummyTokenValue
            })
        });

        // A role assigned to the user: the deep-graph export starts at the role and reaches the user over the association.
        var roleCkTypeId = new RtCkId<CkTypeId>(RoleCkTypeId);
        var role = await repository.CreateTransientRtEntityByRtCkIdAsync(roleCkTypeId);
        role.SetAttributeValue("Name", AttributeValueTypesDto.String, "ProbeRole-7781");
        role.SetAttributeValue("NormalizedName", AttributeValueTypesDto.String, "PROBEROLE-7781");

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var result = new OperationResult();
        await repository.ApplyChangesAsync(session,
            [EntityUpdateInfo<RtEntity>.CreateInsert(userCkTypeId, user), EntityUpdateInfo<RtEntity>.CreateInsert(roleCkTypeId, role)],
            [
                AssociationUpdateInfo.CreateInsert(new RtEntityId(userCkTypeId, user.RtId),
                    new RtEntityId(roleCkTypeId, role.RtId), new RtCkId<CkAssociationRoleId>("System.Identity/AssignedRole"))
            ], result);
        await Assert.That(result.HasErrors).IsFalse();
        await session.CommitTransactionAsync();
        return new Seed(fixture, repository, user.RtId, role.RtId, logStart, fixture.Logs.Count, fixture.ResetGeneration);
    }

    private static async Task<OctoObjectId> StoreQueryAsync(Seed seed, string? filterPath, RtFieldFilterOperatorEnum op,
        string? comparison, string? sortPath, params string[] columns)
    {
        using var session = await seed.Repository.GetSessionAsync();
        session.StartTransaction();
        var query = await seed.Repository.CreateTransientRtEntityAsync<RtSimpleRtQuery>();
        query.RtId = OctoObjectId.GenerateNewId();
        query.Name = "Users";
        query.QueryCkTypeId = UserCkTypeId;
        foreach (var column in columns.Length == 0 ? ["UserName"] : columns)
        {
            query.Columns.Add(column);
        }

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

        // Stored directly through the repository, as an import or a restore would: the asset repository's store-time check
        // does not run on this path, so the job must not trust the stored query.
        await seed.Repository.InsertOneRtEntityAsync(session, query);
        await session.CommitTransactionAsync();
        return query.RtId;
    }

    private static async Task<string> ExportByQueryAsync(Seed seed, OctoObjectId queryId)
    {
        var provider = seed.Fixture.Provider;
        var command = new ExportRtModelByQueryByQueryCommand(
            provider.GetRequiredService<ILogger<ExportRtModelByQueryByQueryCommand>>(), seed.Fixture.SystemContext,
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

    private static async Task<string> ExportByDeepGraphAsync(Seed seed, OctoObjectId rootId, string rootCkTypeId)
    {
        var provider = seed.Fixture.Provider;
        var command = new ExportRtModelByDeepGraphCommand(
            provider.GetRequiredService<ILogger<ExportRtModelByDeepGraphCommand>>(), seed.Fixture.SystemContext,
            provider.GetRequiredService<ICkCacheService>(), provider.GetRequiredService<IRtEntityToTcDtoConverter>(),
            provider.GetRequiredService<IRtSerializer>());
        var request = new ExportRtByDeepGraphCommandRequest(seed.Fixture.TenantId, [rootId],
            new RtCkId<CkTypeId>(rootCkTypeId));
        var file = Path.GetTempFileName();
        try
        {
            await command.ExportAsync(seed.Fixture.TenantId, request, Caller, file, null);
            return await File.ReadAllTextAsync(file);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static async Task AssertNoDummyValueAsync(string text)
    {
        foreach (var dummy in DummyValues)
        {
            await Assert.That(text).DoesNotContain(dummy);
        }
    }

    /// <summary>
    ///     No log line of the seed write and none written since <paramref name="testStart" /> carries a dummy value.
    ///     Non-vacuous: the MongoDB command log of the seed write (it names the user) must have been captured.
    /// </summary>
    private static async Task AssertNoDummyValueInLogsAsync(Seed seed, int testStart)
    {
        var seedLines = seed.Fixture.Logs.Since(seed.LogStart).Take(seed.LogEnd - seed.LogStart).ToList();
        await Assert.That(seedLines.Any(l => l.Contains(ProbeUserName, StringComparison.Ordinal))).IsTrue();
        var lines = seedLines.Concat(seed.Fixture.Logs.Since(testStart)).ToList();
        var leaking = lines.Where(l => DummyValues.Any(d => l.Contains(d, StringComparison.Ordinal))).ToList();
        await Assert.That(leaking).IsEmpty();
    }

    // ------------------------------------------------------------------------------------------- export by query

    [Test]
    public async Task ExportByQuery_OfTheUsers_ExportsTheUser_WithoutAnyCredential()
    {
        var seed = await SeedAsync();
        var logStart = seed.Fixture.Logs.Count;
        var query = await StoreQueryAsync(seed, "UserName", RtFieldFilterOperatorEnum.Equals, ProbeUserName, "UserName");

        var exported = await ExportByQueryAsync(seed, query);

        await Assert.That(exported).Contains(ProbeUserName);
        await AssertNoDummyValueAsync(exported);
        await AssertNoDummyValueInLogsAsync(seed, logStart);
    }

    [Test]
    public async Task ExportByQuery_WithHiddenColumnsInTheStoredQuery_StillExportsNoCredential()
    {
        // The export writes whole entities (the columns of the query do not select attributes); a stored Hidden column must
        // not change that.
        var seed = await SeedAsync();
        var logStart = seed.Fixture.Logs.Count;
        var query = await StoreQueryAsync(seed, null, RtFieldFilterOperatorEnum.Equals, null, null,
            "UserName", "PasswordHash", "SecurityStamp", "UserTokens");

        var exported = await ExportByQueryAsync(seed, query);

        await Assert.That(exported).Contains(ProbeUserName);
        await AssertNoDummyValueAsync(exported);
        await AssertNoDummyValueInLogsAsync(seed, logStart);
    }

    [Test]
    [Arguments("PasswordHash", RtFieldFilterOperatorEnum.Equals, DummyPasswordHash)]
    [Arguments("passwordHash", RtFieldFilterOperatorEnum.Like, "DUMMY%")]
    [Arguments("PasswordHash", RtFieldFilterOperatorEnum.MatchRegEx, "^DUMMY")]
    [Arguments("PasswordHash", RtFieldFilterOperatorEnum.GreaterThan, "D")]
    [Arguments("SecurityStamp", RtFieldFilterOperatorEnum.Equals, DummySecurityStamp)]
    [Arguments("UserTokens.Value", RtFieldFilterOperatorEnum.Equals, DummyTokenValue)]
    [Arguments("userTokens.value", RtFieldFilterOperatorEnum.Like, "DUMMY%")]
    public async Task ExportByQuery_WithAStoredFilterOnACredential_IsRefused_ForTheRightAndAWrongGuessAlike(string path,
        RtFieldFilterOperatorEnum op, string comparison)
    {
        var seed = await SeedAsync();
        var logStart = seed.Fixture.Logs.Count;
        var right = await StoreQueryAsync(seed, path, op, comparison, null);
        var wrong = await StoreQueryAsync(seed, path, op, "wrong-guess", null);
        // The stored guess itself is in the command log of the query insert above (the test wrote it); from here on no
        // line may carry a dummy value.
        logStart = seed.Fixture.Logs.Count;

        var rightFailure = await Assert.That(async () => await ExportByQueryAsync(seed, right))
            .Throws<CommandExecutionFailedException>();
        var wrongFailure = await Assert.That(async () => await ExportByQueryAsync(seed, wrong))
            .Throws<CommandExecutionFailedException>();

        await Assert.That(rightFailure!.Message).Contains("ATTRIBUTE_NOT_QUERYABLE").And.Contains("field filter");
        await Assert.That(wrongFailure!.Message).Contains("ATTRIBUTE_NOT_QUERYABLE");
        await AssertNoDummyValueAsync(rightFailure.ToString());
        await AssertNoDummyValueInLogsAsync(seed, logStart);
    }

    [Test]
    [Arguments("PasswordHash")]
    [Arguments("SecurityStamp")]
    [Arguments("UserTokens.Value")]
    public async Task ExportByQuery_WithAStoredSortOnACredential_IsRefused(string path)
    {
        var seed = await SeedAsync();
        var logStart = seed.Fixture.Logs.Count;
        var query = await StoreQueryAsync(seed, null, RtFieldFilterOperatorEnum.Equals, null, path);

        var failure = await Assert.That(async () => await ExportByQueryAsync(seed, query))
            .Throws<CommandExecutionFailedException>();

        await Assert.That(failure!.Message).Contains("ATTRIBUTE_NOT_QUERYABLE").And.Contains("sort order");
        await AssertNoDummyValueInLogsAsync(seed, logStart);
    }

    // ------------------------------------------------------------------------------------------- export by deep graph

    [Test]
    public async Task ExportByDeepGraph_FromTheUser_ExportsTheUser_WithoutAnyCredential()
    {
        var seed = await SeedAsync();
        var logStart = seed.Fixture.Logs.Count;

        var exported = await ExportByDeepGraphAsync(seed, seed.User, UserCkTypeId);

        await Assert.That(exported).Contains(ProbeUserName);
        await AssertNoDummyValueAsync(exported);
        await AssertNoDummyValueInLogsAsync(seed, logStart);
    }

    [Test]
    public async Task ExportByDeepGraph_FromTheAssignedRole_ExportsTheRole_WithoutAnyCredential()
    {
        var seed = await SeedAsync();
        var logStart = seed.Fixture.Logs.Count;

        var exported = await ExportByDeepGraphAsync(seed, seed.Role, RoleCkTypeId);

        await Assert.That(exported).Contains("ProbeRole-7781");
        await AssertNoDummyValueAsync(exported);
        await AssertNoDummyValueInLogsAsync(seed, logStart);
    }
}
