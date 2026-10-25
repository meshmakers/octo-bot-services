using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Runtime.Contracts.TransportContainer;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ExportTestCkModel.Generated.ExportTest.v1;

namespace Meshmakers.Octo.Backend.Jobs.IntegrationTests;

/// <summary>
///     AB#6734 — the two runtime-data export commands against a real engine and MongoDB with a restricted policy table:
///     the exported file contains only what the requesting caller may read on the normal API. The Country type is the
///     restricted one; Management may read it, employees only their own rows (owned-only) or nothing.
/// </summary>
/// <remarks>
///     The commands are the very classes the Hangfire jobs run; nothing is mocked below them. The tests of one class
///     share one tenant, so they run one after the other (TUnit <c>NotInParallel</c>).
/// </remarks>
[NotInParallel]
public class ExportPermissionTests
{
    private static readonly RtSecurityContext Management = RtSecurityContext.ForUser("user-m", ["TestManagement"]);
    private static readonly RtSecurityContext Employee1 = RtSecurityContext.ForUser("user-1", ["TestEmployee"]);
    private static readonly RtSecurityContext Employee2 = RtSecurityContext.ForUser("user-2", ["TestEmployee"]);
    private static readonly RtSecurityContext Outsider = RtSecurityContext.ForUser("user-x", ["TestOther"]);

    private static string CountryTypeId => ExportTestCkIds.RtCkCountryTypeId.SemanticVersionedFullName;

    private static RtDataPolicyTable ManagementOnly() => new(
    [
        new RtDataPolicyRule("export.countries", new HashSet<string> { CountryTypeId }, [RtDataAction.Read],
            OwnedOnly: false, AuditOnly: false, new HashSet<string> { "TestManagement" })
    ]);

    private static RtDataPolicyTable ManagementAndOwnedEmployees() => new(
    [
        new RtDataPolicyRule("export.countries", new HashSet<string> { CountryTypeId }, [RtDataAction.Read],
            OwnedOnly: false, AuditOnly: false, new HashSet<string> { "TestManagement" }),
        new RtDataPolicyRule("export.countries", new HashSet<string> { CountryTypeId }, [RtDataAction.Read],
            OwnedOnly: true, AuditOnly: false, new HashSet<string> { "TestEmployee" })
    ]);

    private sealed record Seed(
        ExportEngineFixture Fixture,
        OctoObjectId Continent,
        OctoObjectId Austria,
        OctoObjectId Germany,
        OctoObjectId Query);

    private static async Task<Seed> SeedAsync()
    {
        var fixture = await ExportEngineFixture.GetAsync();
        await fixture.ResetAsync();
        var repository = await fixture.SystemContext.FindTenantRepositoryAsync(fixture.TenantId);

        var continent = OctoObjectId.GenerateNewId();
        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            var rtContinent = await repository.CreateTransientRtEntityAsync<RtContinent>();
            rtContinent.RtId = continent;
            rtContinent.Name = "Continent-Europe";
            await repository.InsertOneRtEntityAsync(session, rtContinent);

            var query = await repository.CreateTransientRtEntityAsync<RtSimpleRtQuery>();
            query.RtId = OctoObjectId.GenerateNewId();
            query.Name = "Countries";
            query.QueryCkTypeId = ExportTestCkIds.RtCkCountryTypeId.SemanticVersionedFullName;
            query.Columns.Add("Name");
            await repository.InsertOneRtEntityAsync(session, query);
            await session.CommitTransactionAsync();

            var austria = await InsertCountryAsync(repository, Employee1, "Country-Austria", continent);
            var germany = await InsertCountryAsync(repository, Employee2, "Country-Germany", continent);
            return new Seed(fixture, continent, austria, germany, query.RtId);
        }
    }

    private static async Task<OctoObjectId> InsertCountryAsync(ITenantRepository repository,
        RtSecurityContext creator, string name, OctoObjectId parentContinent)
    {
        using var session = await repository.GetSessionAsync(creator);
        session.StartTransaction();
        var country = await repository.CreateTransientRtEntityAsync<RtCountry>();
        country.RtId = OctoObjectId.GenerateNewId();
        country.Name = name;
        var result = new OperationResult();
        await repository.ApplyChangesAsync(session,
            new List<IEntityUpdateInfo<RtEntity>> { EntityUpdateInfo<RtEntity>.CreateInsert(country) },
            [
                AssociationUpdateInfo.CreateInsert(
                    new RtEntityId(ExportTestCkIds.RtCkCountryTypeId, country.RtId),
                    new RtEntityId(ExportTestCkIds.RtCkContinentTypeId, parentContinent),
                    SystemCkIds.RtCkParentChildRoleId)
            ], result);
        await session.CommitTransactionAsync();
        await Assert.That(result.HasErrors).IsFalse();
        return country.RtId;
    }

    private static async Task<string> ExportByQueryAsync(Seed seed, RtSecurityContext caller)
    {
        var provider = seed.Fixture.Provider;
        var command = new ExportRtModelByQueryByQueryCommand(
            NullLogger<ExportRtModelByQueryByQueryCommand>.Instance, seed.Fixture.SystemContext,
            provider.GetRequiredService<ICkCacheService>(), provider.GetRequiredService<IRtEntityToTcDtoConverter>(),
            provider.GetRequiredService<IRtSerializer>());
        var file = Path.GetTempFileName();
        try
        {
            await command.ExportAsync(seed.Fixture.TenantId, seed.Query, caller, file, null);
            return await File.ReadAllTextAsync(file);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static async Task<string> ExportByDeepGraphAsync(Seed seed, RtSecurityContext caller)
    {
        var provider = seed.Fixture.Provider;
        var command = new ExportRtModelByDeepGraphCommand(
            NullLogger<ExportRtModelByDeepGraphCommand>.Instance, seed.Fixture.SystemContext,
            provider.GetRequiredService<ICkCacheService>(), provider.GetRequiredService<IRtEntityToTcDtoConverter>(),
            provider.GetRequiredService<IRtSerializer>());
        var request = new ExportRtByDeepGraphCommandRequest(seed.Fixture.TenantId, [seed.Continent],
            ExportTestCkIds.RtCkContinentTypeId);
        var file = Path.GetTempFileName();
        try
        {
            await command.ExportAsync(seed.Fixture.TenantId, request, caller, file, null);
            return await File.ReadAllTextAsync(file);
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Export by query
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Query_CallerDeniedOnTheType_GetsNoRows_CallerAllowedGetsAll()
    {
        var seed = await SeedAsync();
        seed.Fixture.Resolver.Table = ManagementOnly();

        var denied = await ExportByQueryAsync(seed, Employee1);
        var outsider = await ExportByQueryAsync(seed, Outsider);
        var allowed = await ExportByQueryAsync(seed, Management);

        await Assert.That(denied).DoesNotContain("Country-Austria").And.DoesNotContain("Country-Germany");
        await Assert.That(outsider).DoesNotContain("Country-Austria").And.DoesNotContain("Country-Germany");
        await Assert.That(allowed).Contains("Country-Austria").And.Contains("Country-Germany");
    }

    [Test]
    public async Task Query_OwnedOnlyPolicy_ReturnsOnlyTheCallersOwnRows()
    {
        var seed = await SeedAsync();
        seed.Fixture.Resolver.Table = ManagementAndOwnedEmployees();

        var employee1 = await ExportByQueryAsync(seed, Employee1);
        var employee2 = await ExportByQueryAsync(seed, Employee2);
        var management = await ExportByQueryAsync(seed, Management);

        await Assert.That(employee1).Contains("Country-Austria").And.DoesNotContain("Country-Germany");
        await Assert.That(employee2).Contains("Country-Germany").And.DoesNotContain("Country-Austria");
        await Assert.That(management).Contains("Country-Austria").And.Contains("Country-Germany");
    }

    [Test]
    public async Task Query_TenantWithoutPolicies_ExportIsTheSameForEveryCaller()
    {
        var seed = await SeedAsync();

        var asEmployee = await ExportByQueryAsync(seed, Employee1);
        var asOutsider = await ExportByQueryAsync(seed, Outsider);

        await Assert.That(asEmployee).Contains("Country-Austria").And.Contains("Country-Germany");
        await Assert.That(asOutsider).IsEqualTo(asEmployee);
        await Assert.That(asEmployee.IndexOf(seed.Austria.ToString(), StringComparison.Ordinal))
            .IsLessThan(asEmployee.IndexOf(seed.Germany.ToString(), StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------
    // Export by deep graph
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task DeepGraph_CallerDeniedOnTheType_GetsNeitherEntitiesNorAssociationsToThem()
    {
        var seed = await SeedAsync();
        seed.Fixture.Resolver.Table = ManagementOnly();

        var denied = await ExportByDeepGraphAsync(seed, Employee1);
        var allowed = await ExportByDeepGraphAsync(seed, Management);

        await Assert.That(denied).Contains("Continent-Europe");
        await Assert.That(denied).DoesNotContain("Country-Austria").And.DoesNotContain("Country-Germany");
        await Assert.That(denied).DoesNotContain(seed.Austria.ToString()).And.DoesNotContain(seed.Germany.ToString());
        await Assert.That(allowed).Contains("Country-Austria").And.Contains("Country-Germany");
        await Assert.That(allowed).Contains(seed.Austria.ToString()).And.Contains(seed.Germany.ToString());
    }

    [Test]
    public async Task DeepGraph_OwnedOnlyPolicy_KeepsOnlyTheCallersOwnEntitiesAndTheirAssociations()
    {
        var seed = await SeedAsync();
        seed.Fixture.Resolver.Table = ManagementAndOwnedEmployees();

        var employee1 = await ExportByDeepGraphAsync(seed, Employee1);

        await Assert.That(employee1).Contains("Country-Austria").And.DoesNotContain("Country-Germany");
        await Assert.That(employee1).Contains(seed.Austria.ToString()).And.DoesNotContain(seed.Germany.ToString());
    }

    [Test]
    public async Task DeepGraph_TenantWithoutPolicies_ExportIsTheSameForEveryCaller()
    {
        var seed = await SeedAsync();

        var asEmployee = await ExportByDeepGraphAsync(seed, Employee1);
        var asOutsider = await ExportByDeepGraphAsync(seed, Outsider);

        await Assert.That(asEmployee).Contains("Country-Austria").And.Contains("Country-Germany");
        // AB#7471: byte-identical on purpose - the export orders entities by CK type and RtId (the database returns
        // the deep graph in no stable order), so users can diff two exports. Austria was inserted first, so its
        // RtId sorts first.
        await Assert.That(asOutsider).IsEqualTo(asEmployee);
        await Assert.That(asEmployee.IndexOf(seed.Austria.ToString(), StringComparison.Ordinal))
            .IsLessThan(asEmployee.IndexOf(seed.Germany.ToString(), StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------
    // Fail closed
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task SystemContext_IsRefused_AndProducesNoFile()
    {
        var seed = await SeedAsync();

        await Assert.That(async () => await ExportByQueryAsync(seed, RtSecurityContext.System))
            .Throws<JobFailedException>();
        await Assert.That(async () => await ExportByDeepGraphAsync(seed, RtSecurityContext.System))
            .Throws<JobFailedException>();
    }
}
