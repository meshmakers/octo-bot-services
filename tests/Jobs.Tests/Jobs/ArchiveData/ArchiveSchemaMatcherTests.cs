using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs.ArchiveData;

public class ArchiveSchemaMatcherTests
{
    private static ArchiveSchemaDto Raw(
        string ckType = "Sensor",
        IReadOnlyList<ArchiveColumnDto>? columns = null,
        string kind = "raw",
        IReadOnlyList<ArchiveRollupAggregationDto>? rollups = null,
        string? wellKnownName = "voltage-raw")
    {
        return new ArchiveSchemaDto(
            RtId: "665f00000000000000000e21",
            RtWellKnownName: wellKnownName,
            Kind: kind,
            TargetCkTypeId: ckType,
            Columns: columns ?? new[]
            {
                new ArchiveColumnDto("voltage", true, false),
                new ArchiveColumnDto("current", false, false)
            },
            RollupAggregations: rollups,
            PeriodMs: null);
    }

    [Test]
    public async Task FindMismatch_IdenticalSchemas_ReturnsNull()
    {
        var result = ArchiveSchemaMatcher.FindMismatch(Raw(), Raw());

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task FindMismatch_ColumnOrderDiffers_StillMatches()
    {
        var source = Raw(columns: new[]
        {
            new ArchiveColumnDto("current", false, false),
            new ArchiveColumnDto("voltage", true, false)
        });

        var result = ArchiveSchemaMatcher.FindMismatch(source, Raw());

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task FindMismatch_DifferentCkType_ReportsCkType()
    {
        var result = ArchiveSchemaMatcher.FindMismatch(Raw(ckType: "Meter"), Raw(ckType: "Sensor"));

        await Assert.That(result).IsNotNull();
        await Assert.That(result!).Contains("Meter");
        await Assert.That(result!).Contains("Sensor");
    }

    [Test]
    public async Task FindMismatch_DifferentKind_ReportsKind()
    {
        var result = ArchiveSchemaMatcher.FindMismatch(Raw(kind: "timeRange"), Raw(kind: "raw"));

        await Assert.That(result).IsNotNull();
        await Assert.That(result!).Contains("timeRange");
        await Assert.That(result!).Contains("raw");
    }

    [Test]
    public async Task FindMismatch_TargetHasExtraColumn_NamesMissingColumn()
    {
        var source = Raw(columns: new[]
        {
            new ArchiveColumnDto("voltage", true, false)
        });

        var result = ArchiveSchemaMatcher.FindMismatch(source, Raw());

        await Assert.That(result).IsNotNull();
        await Assert.That(result!).Contains("current");
    }

    [Test]
    public async Task FindMismatch_SourceHasExtraColumn_NamesUnexpectedColumn()
    {
        var source = Raw(columns: new[]
        {
            new ArchiveColumnDto("voltage", true, false),
            new ArchiveColumnDto("current", false, false),
            new ArchiveColumnDto("phase", false, false)
        });

        var result = ArchiveSchemaMatcher.FindMismatch(source, Raw());

        await Assert.That(result).IsNotNull();
        await Assert.That(result!).Contains("phase");
    }

    [Test]
    public async Task FindMismatch_DifferentIndexedFlag_NamesColumn()
    {
        var source = Raw(columns: new[]
        {
            new ArchiveColumnDto("voltage", false, false),
            new ArchiveColumnDto("current", false, false)
        });

        var result = ArchiveSchemaMatcher.FindMismatch(source, Raw());

        await Assert.That(result).IsNotNull();
        await Assert.That(result!).Contains("voltage");
        await Assert.That(result!).Contains("indexed");
    }

    [Test]
    public async Task FindMismatch_DifferentRequiredFlag_NamesColumn()
    {
        var source = Raw(columns: new[]
        {
            new ArchiveColumnDto("voltage", true, false),
            new ArchiveColumnDto("current", false, true)
        });

        var result = ArchiveSchemaMatcher.FindMismatch(source, Raw());

        await Assert.That(result).IsNotNull();
        await Assert.That(result!).Contains("current");
        await Assert.That(result!).Contains("required");
    }

    [Test]
    public async Task FindMismatch_DuplicateColumnInExport_IsRejected()
    {
        var source = Raw(columns: new[]
        {
            new ArchiveColumnDto("voltage", true, false),
            new ArchiveColumnDto("voltage", true, false),
            new ArchiveColumnDto("current", false, false)
        });

        var result = ArchiveSchemaMatcher.FindMismatch(source, Raw());

        await Assert.That(result).IsNotNull();
        await Assert.That(result!).Contains("voltage");
        await Assert.That(result!).Contains("more than once");
    }

    [Test]
    public async Task FindMismatch_MatchingRollupAggregations_ReturnsNull()
    {
        var aggregations = new[]
        {
            new ArchiveRollupAggregationDto("voltage", "avg", "voltage_avg"),
            new ArchiveRollupAggregationDto("voltage", "max", "voltage_max")
        };

        var source = Raw(kind: "rollup", rollups: new[]
        {
            // reversed order — order-independent match expected
            new ArchiveRollupAggregationDto("voltage", "max", "voltage_max"),
            new ArchiveRollupAggregationDto("voltage", "avg", "voltage_avg")
        });
        var target = Raw(kind: "rollup", rollups: aggregations);

        var result = ArchiveSchemaMatcher.FindMismatch(source, target);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task FindMismatch_RollupAggregationCountDiffers_IsReported()
    {
        var source = Raw(kind: "rollup", rollups: new[]
        {
            new ArchiveRollupAggregationDto("voltage", "avg", "voltage_avg")
        });
        var target = Raw(kind: "rollup", rollups: new[]
        {
            new ArchiveRollupAggregationDto("voltage", "avg", "voltage_avg"),
            new ArchiveRollupAggregationDto("voltage", "max", "voltage_max")
        });

        var result = ArchiveSchemaMatcher.FindMismatch(source, target);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!).Contains("aggregation");
    }

    [Test]
    public async Task FindMismatch_RollupAggregationFunctionDiffers_NamesIt()
    {
        var source = Raw(kind: "rollup", rollups: new[]
        {
            new ArchiveRollupAggregationDto("voltage", "min", "voltage_min")
        });
        var target = Raw(kind: "rollup", rollups: new[]
        {
            new ArchiveRollupAggregationDto("voltage", "avg", "voltage_avg")
        });

        var result = ArchiveSchemaMatcher.FindMismatch(source, target);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!).Contains("voltage");
    }

    [Test]
    public async Task FindMismatch_RollupTargetColumnNameDiffers_IsReported()
    {
        var source = Raw(kind: "rollup", rollups: new[]
        {
            new ArchiveRollupAggregationDto("voltage", "avg", "v_avg")
        });
        var target = Raw(kind: "rollup", rollups: new[]
        {
            new ArchiveRollupAggregationDto("voltage", "avg", "voltage_avg")
        });

        var result = ArchiveSchemaMatcher.FindMismatch(source, target);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!).Contains("v_avg");
        await Assert.That(result!).Contains("voltage_avg");
    }

    [Test]
    public async Task FindMismatch_RawArchiveIgnoresRollupAggregations()
    {
        // For a raw archive, RollupAggregations differences must not matter.
        var source = Raw(rollups: new[]
        {
            new ArchiveRollupAggregationDto("voltage", "avg", "voltage_avg")
        });
        var target = Raw(rollups: null);

        var result = ArchiveSchemaMatcher.FindMismatch(source, target);

        await Assert.That(result).IsNull();
    }
}
