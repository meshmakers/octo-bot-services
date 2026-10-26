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
    ///     Builds the file of step 5 of the bug: what a file exported BEFORE the AB#7446 fix looks like, i.e. data point
    ///     records without their required <c>ExternalId</c>. Leaves the tenant empty, ready for the import.
    /// </summary>
    private static async Task<(string File, OctoObjectId RtId)> ExportWithoutExternalIdAsync(ExportEngineFixture fixture)
    {
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
        return (withoutExternalId, rtId);
    }

    /// <summary>
    ///     AB#7450 (default mode, <c>Import:StrictMandatoryValidation</c> off): a file whose records lack the required
    ///     <c>ExternalId</c> is still imported - existing imports, blueprint applies and restores stay unchanged - but
    ///     the import now reports it: one audit event for the affected entity naming every record member with its path.
    /// </summary>
    [Test]
    public async Task Import_OfAFileWhoseRecordsLackTheRequiredExternalId_IsAcceptedAndReported()
    {
        var fixture = await ExportEngineFixture.GetAsync();
        var (file, rtId) = await ExportWithoutExternalIdAsync(fixture);

        await ImportAsync(fixture, file);

        var states = await ReadStatesAsync(fixture, rtId);
        await Assert.That(states.Count).IsEqualTo(2);
        await Assert.That(states.All(s => string.IsNullOrEmpty((string?)s.GetAttributeValueOrDefault("ExternalId")))).IsTrue();

        var reported = fixture.AuditTrail.MissingMandatory;
        await Assert.That(reported.Count).IsEqualTo(1);
        await Assert.That(reported[0].RtId).IsEqualTo(rtId);
        await Assert.That(reported[0].Missing.Count).IsEqualTo(2);
        await Assert.That(reported[0].Missing.Any(m => m.StartsWith("ExportTest", StringComparison.Ordinal) &&
                                                       m.EndsWith("/ExternalId-1 at States[0]", StringComparison.Ordinal))).IsTrue();
        await Assert.That(reported[0].Missing.Any(m => m.EndsWith(" at States[1]", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>
    ///     AB#7450 (strict mode): the same file is rejected before anything is written - no entity of the file is stored.
    /// </summary>
    [Test]
    public async Task Import_OfAFileWhoseRecordsLackTheRequiredExternalId_IsRejectedInStrictMode()
    {
        var fixture = await ExportEngineFixture.GetAsync();
        var (file, rtId) = await ExportWithoutExternalIdAsync(fixture);
        fixture.ImportOptions.StrictMandatoryValidation = true;
        try
        {
            var exception = await Assert.ThrowsAsync<ExchangeException>(() => ImportAsync(fixture, file));
            await Assert.That(exception!.Message).Contains("States[0]");
        }
        finally
        {
            fixture.ImportOptions.StrictMandatoryValidation = false;
        }

        var repository = await fixture.SystemContext.FindTenantRepositoryAsync(fixture.TenantId);
        using var session = await repository.GetSessionAsync();
        var result = await repository.GetRtEntitiesByIdAsync(session, ExportTestCkIds.RtCkControlTypeId, [rtId],
            RtEntityQueryOptions.Create());
        await Assert.That(result.Items.Count()).IsEqualTo(0);
    }

    /// <summary>
    ///     AB#7450: a file that carries the identity of every record produces no report.
    /// </summary>
    [Test]
    public async Task Import_OfACompleteFile_ReportsNothing()
    {
        var fixture = await ExportEngineFixture.GetAsync();
        await fixture.ResetAsync();
        var rtId = await InsertControlAsync(fixture);
        var exported = await ExportByDeepGraphAsync(fixture, rtId);
        await fixture.ResetAsync();

        await ImportAsync(fixture, exported);

        await Assert.That(fixture.AuditTrail.MissingMandatory.Count).IsEqualTo(0);
    }
}
