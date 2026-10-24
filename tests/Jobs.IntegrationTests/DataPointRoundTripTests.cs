using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Runtime.Contracts.TransportContainer;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ExportTestCkModel.Generated.ExportTest.v1;

namespace Meshmakers.Octo.Backend.Jobs.IntegrationTests;

/// <summary>
///     AB#7446 — export → import round trip of an entity holding a seed-owned RecordArray whose records have members of
///     mixed ownership (<c>DataPointSample</c>: the shape and ownership of <c>System.Communication/DataPoint</c> as held
///     by <c>Loxone/Control.States</c>). Against a real engine and MongoDB, through the very commands the Hangfire jobs run.
/// </summary>
/// <remarks>
///     What the file must carry: <c>Name</c> and <c>ExternalId</c> of every record (seed-owned: the identity of the
///     data point, a required member). What it must not carry: <c>CurrentValue</c> and <c>LastUpdate</c> (runtime
///     state, live values - AB#5187). After the import into an empty tenant the records have their identity and the
///     live values are empty until the next message fills them.
/// </remarks>
[NotInParallel]
public class DataPointRoundTripTests
{
    private static readonly RtSecurityContext Caller = RtSecurityContext.ForUser("user-m", ["TestManagement"]);

    private static RtRecord Sample(string name, string externalId, string currentValue) =>
        NewRecord(r =>
        {
            r.SetAttributeRawValue("Name", name);
            r.SetAttributeRawValue("ExternalId", externalId);
            r.SetAttributeRawValue("CurrentValue", currentValue);
            r.SetAttributeRawValue("LastUpdate", new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc));
        });

    private static RtRecord NewRecord(Action<RtRecord> init)
    {
        var record = new RtRecord { CkRecordId = ExportTestCkIds.RtCkDataPointSampleRecordId };
        init(record);
        return record;
    }

    private static async Task<OctoObjectId> InsertControlAsync(ExportEngineFixture fixture)
    {
        var repository = await fixture.SystemContext.FindTenantRepositoryAsync(fixture.TenantId);
        var control = new RtEntity { RtId = OctoObjectId.GenerateNewId(), CkTypeId = ExportTestCkIds.RtCkControlTypeId };
        control.SetAttributeRawValue("Name", "Control-Kitchen");
        control.SetAttributeRawValue("States", new List<object>
        {
            Sample("Temperature", "state-uuid-1", "21.5"),
            Sample("Humidity", "state-uuid-2", "48")
        });
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        await repository.InsertOneRtEntityAsync(session, ExportTestCkIds.RtCkControlTypeId, control);
        await session.CommitTransactionAsync();
        return control.RtId;
    }

    private static async Task<string> ExportByDeepGraphAsync(ExportEngineFixture fixture, OctoObjectId rtId)
    {
        var provider = fixture.Provider;
        var command = new ExportRtModelByDeepGraphCommand(
            NullLogger<ExportRtModelByDeepGraphCommand>.Instance, fixture.SystemContext,
            provider.GetRequiredService<ICkCacheService>(), provider.GetRequiredService<IRtEntityToTcDtoConverter>(),
            provider.GetRequiredService<IRtSerializer>());
        var request = new ExportRtByDeepGraphCommandRequest(fixture.TenantId, [rtId], ExportTestCkIds.RtCkControlTypeId);
        var file = Path.GetTempFileName();
        try
        {
            await command.ExportAsync(fixture.TenantId, request, Caller, file, null);
            return await File.ReadAllTextAsync(file);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static async Task<string> ExportByQueryAsync(ExportEngineFixture fixture)
    {
        var repository = await fixture.SystemContext.FindTenantRepositoryAsync(fixture.TenantId);
        var query = await repository.CreateTransientRtEntityAsync<RtSimpleRtQuery>();
        query.RtId = OctoObjectId.GenerateNewId();
        query.Name = "Controls";
        query.QueryCkTypeId = ExportTestCkIds.RtCkControlTypeId.SemanticVersionedFullName;
        query.Columns.Add("Name");
        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            await repository.InsertOneRtEntityAsync(session, query);
            await session.CommitTransactionAsync();
        }

        var provider = fixture.Provider;
        var command = new ExportRtModelByQueryByQueryCommand(
            NullLogger<ExportRtModelByQueryByQueryCommand>.Instance, fixture.SystemContext,
            provider.GetRequiredService<ICkCacheService>(), provider.GetRequiredService<IRtEntityToTcDtoConverter>(),
            provider.GetRequiredService<IRtSerializer>());
        var file = Path.GetTempFileName();
        try
        {
            await command.ExportAsync(fixture.TenantId, query.RtId, Caller, file, null);
            return await File.ReadAllTextAsync(file);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static async Task ImportAsync(ExportEngineFixture fixture, string exported)
    {
        var repository = await fixture.SystemContext.FindTenantRepositoryAsync(fixture.TenantId);
        // ResetAsync cleared the tenant: like a service start, load the CK cache of the (re-created) tenant first.
        var cache = fixture.Provider.GetRequiredService<ICkCacheService>();
        if (!cache.IsTenantLoaded(fixture.TenantId))
        {
            await repository.LoadCacheForTenantAsync(cache);
        }

        await fixture.Provider.GetRequiredService<IImportRtModelCommand>()
            .ImportTextAsync(repository, exported, ImportStrategy.Upsert);
    }

    private static async Task<IReadOnlyList<RtRecord>> ReadStatesAsync(ExportEngineFixture fixture, OctoObjectId rtId)
    {
        var repository = await fixture.SystemContext.FindTenantRepositoryAsync(fixture.TenantId);
        using var session = await repository.GetSessionAsync();
        var result = await repository.GetRtEntitiesByIdAsync(session, ExportTestCkIds.RtCkControlTypeId, [rtId],
            RtEntityQueryOptions.Create());
        var control = result.Items.Single();
        return ((IEnumerable<object>)control.GetAttributeValueOrDefault("States")!).Cast<RtRecord>().ToList();
    }

    [Test]
    public async Task DeepGraphExport_CarriesNameAndExternalId_NotTheLiveValues()
    {
        var fixture = await ExportEngineFixture.GetAsync();
        await fixture.ResetAsync();
        var rtId = await InsertControlAsync(fixture);

        var exported = await ExportByDeepGraphAsync(fixture, rtId);

        await Assert.That(exported).Contains("state-uuid-1").And.Contains("state-uuid-2");
        await Assert.That(exported).Contains("Temperature").And.Contains("Humidity");
        await Assert.That(exported).DoesNotContain("21.5").And.DoesNotContain("CurrentValue");
        await Assert.That(exported).DoesNotContain("LastUpdate");
    }

    [Test]
    public async Task QueryExport_CarriesNameAndExternalId_NotTheLiveValues()
    {
        var fixture = await ExportEngineFixture.GetAsync();
        await fixture.ResetAsync();
        await InsertControlAsync(fixture);

        var exported = await ExportByQueryAsync(fixture);

        await Assert.That(exported).Contains("state-uuid-1").And.Contains("state-uuid-2");
        await Assert.That(exported).DoesNotContain("21.5").And.DoesNotContain("CurrentValue");
    }

    [Test]
    public async Task RoundTrip_DeepGraph_IntoAnEmptyTenant_KeepsTheIdentityOfEveryDataPoint()
    {
        var fixture = await ExportEngineFixture.GetAsync();
        await fixture.ResetAsync();
        var rtId = await InsertControlAsync(fixture);
        var exported = await ExportByDeepGraphAsync(fixture, rtId);

        await fixture.ResetAsync(); // a fresh tenant: nothing of the control is left
        await ImportAsync(fixture, exported);

        var states = await ReadStatesAsync(fixture, rtId);
        await Assert.That(states.Count).IsEqualTo(2);
        await Assert.That(states.Select(s => (string)s.GetAttributeValueOrDefault("Name")!).Order().ToArray())
            .IsEquivalentTo(new[] { "Humidity", "Temperature" });
        await Assert.That(states.Select(s => (string)s.GetAttributeValueOrDefault("ExternalId")!).Order().ToArray())
            .IsEquivalentTo(new[] { "state-uuid-1", "state-uuid-2" });
        // The live values are not part of the portable definition: empty until the next message.
        await Assert.That(states.All(s => s.GetAttributeValueOrDefault("CurrentValue") == null)).IsTrue();
        await Assert.That(states.All(s => s.GetAttributeValueOrDefault("LastUpdate") == null)).IsTrue();
    }

    [Test]
    public async Task RoundTrip_Query_IntoAnEmptyTenant_KeepsTheIdentityOfEveryDataPoint()
    {
        var fixture = await ExportEngineFixture.GetAsync();
        await fixture.ResetAsync();
        var rtId = await InsertControlAsync(fixture);
        var exported = await ExportByQueryAsync(fixture);

        await fixture.ResetAsync();
        await ImportAsync(fixture, exported);

        var states = await ReadStatesAsync(fixture, rtId);
        await Assert.That(states.Select(s => (string)s.GetAttributeValueOrDefault("ExternalId")!).Order().ToArray())
            .IsEquivalentTo(new[] { "state-uuid-1", "state-uuid-2" });
    }

    /// <summary>
    ///     Step 5 of the bug: what an import does with a file exported BEFORE the fix, i.e. records without their
    ///     required <c>ExternalId</c>. KNOWN GAP, pinned as observed: the engine does not validate the required members
    ///     of a record on import, so the file is accepted and the data points are stored WITHOUT their identity
    ///     (nothing fails, nothing is logged). If the engine starts rejecting such records this test is to be changed
    ///     to expect the rejection - that is the better behaviour.
    /// </summary>
    [Test]
    public async Task Import_OfAFileWhoseRecordsLackTheRequiredExternalId_IsAcceptedWithoutValidation_KnownGap()
    {
        var fixture = await ExportEngineFixture.GetAsync();
        await fixture.ResetAsync();
        var rtId = await InsertControlAsync(fixture);
        var exported = await ExportByDeepGraphAsync(fixture, rtId);
        var lines = exported.Split('\n').ToList();
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].TrimStart().StartsWith("- id: ExportTest/ExternalId", StringComparison.Ordinal))
            {
                lines.RemoveRange(i, 2); // the id line and its value line
            }
        }

        var withoutExternalId = string.Join('\n', lines);
        await Assert.That(withoutExternalId).DoesNotContain("state-uuid");
        await fixture.ResetAsync();

        await ImportAsync(fixture, withoutExternalId);

        var states = await ReadStatesAsync(fixture, rtId);
        await Assert.That(states.Count).IsEqualTo(2);
        await Assert.That(states.All(s => string.IsNullOrEmpty((string?)s.GetAttributeValueOrDefault("ExternalId")))).IsTrue();
    }
}
