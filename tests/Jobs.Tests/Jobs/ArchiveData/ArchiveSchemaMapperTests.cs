using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs.ArchiveData;

public class ArchiveSchemaMapperTests
{
    private const string ArchiveRtId = "665f00000000000000000e21";

    private static ArchiveSnapshot Snapshot(
        IReadOnlyList<CkArchiveColumnSpec>? columns = null,
        IReadOnlyList<CkRollupAggregationSpec>? rollups = null,
        bool isTimeRange = false,
        TimeSpan? period = null)
    {
        return new ArchiveSnapshot(
            new OctoObjectId(ArchiveRtId),
            new RtCkId<CkTypeId>("System-1.0.0/Sensor"),
            CkArchiveStatus.Activated,
            "voltage-raw",
            columns ?? new[] { new CkArchiveColumnSpec("voltage", true, false) })
        {
            RollupAggregations = rollups,
            IsTimeRange = isTimeRange,
            Period = period
        };
    }

    [Test]
    public async Task ToDto_RawArchive_DerivesRawKindAndColumns()
    {
        var dto = ArchiveSchemaMapper.ToDto(Snapshot());

        await Assert.That(dto.Kind).IsEqualTo("raw");
        await Assert.That(dto.RtId).IsEqualTo(ArchiveRtId);
        await Assert.That(dto.RtWellKnownName).IsEqualTo("voltage-raw");
        await Assert.That(dto.TargetCkTypeId).Contains("Sensor");
        await Assert.That(dto.Columns.Count).IsEqualTo(1);
        await Assert.That(dto.Columns[0].Path).IsEqualTo("voltage");
        await Assert.That(dto.Columns[0].Indexed).IsTrue();
        await Assert.That(dto.RollupAggregations).IsNull();
        await Assert.That(dto.PeriodMs).IsNull();
    }

    [Test]
    public async Task ToDto_TimeRangeArchive_DerivesTimeRangeKindAndPeriodMs()
    {
        var dto = ArchiveSchemaMapper.ToDto(Snapshot(isTimeRange: true, period: TimeSpan.FromMinutes(15)));

        await Assert.That(dto.Kind).IsEqualTo("timeRange");
        await Assert.That(dto.PeriodMs).IsEqualTo(15L * 60 * 1000);
    }

    [Test]
    public async Task ToDto_RollupArchive_DerivesRollupKindAndAggregations()
    {
        var rollups = new[]
        {
            new CkRollupAggregationSpec("voltage", CkRollupFunction.Avg, "voltage_avg"),
            new CkRollupAggregationSpec("voltage", CkRollupFunction.Max, "voltage_max")
        };

        var dto = ArchiveSchemaMapper.ToDto(Snapshot(rollups: rollups));

        await Assert.That(dto.Kind).IsEqualTo("rollup");
        await Assert.That(dto.RollupAggregations).IsNotNull();
        await Assert.That(dto.RollupAggregations!.Count).IsEqualTo(2);
        await Assert.That(dto.RollupAggregations[0].SourcePath).IsEqualTo("voltage");
        await Assert.That(dto.RollupAggregations[0].Function).IsEqualTo("Avg");
        await Assert.That(dto.RollupAggregations[0].TargetColumnName).IsEqualTo("voltage_avg");
    }

    [Test]
    public async Task ToDto_RoundTripsThroughSchemaMatcher()
    {
        // A snapshot mapped to a DTO must validate cleanly against itself (the import self-match case).
        var snapshot = Snapshot(rollups: new[]
        {
            new CkRollupAggregationSpec("voltage", CkRollupFunction.Avg, "voltage_avg")
        });

        var dto = ArchiveSchemaMapper.ToDto(snapshot);

        var mismatch = ArchiveSchemaMatcher.FindMismatch(dto, ArchiveSchemaMapper.ToDto(snapshot));
        await Assert.That(mismatch).IsNull();
    }
}
