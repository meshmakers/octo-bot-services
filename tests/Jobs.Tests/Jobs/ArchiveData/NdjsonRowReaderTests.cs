using System.Text;
using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs.ArchiveData;

/// <summary>
///     The reader must hand the engine CLR values, not <c>JsonElement</c>s — the import path's
///     <c>as string</c> / <c>DateTime</c> pattern matches silently miss on <c>JsonElement</c>, which
///     made every non-empty archive restore fail with "field 'rtid' must be a 24-character hex
///     string, but was '(null)'" (energyiq restore, 2026-08-10).
/// </summary>
public class NdjsonRowReaderTests
{
    private static async Task<List<IReadOnlyDictionary<string, object?>>> ReadAsync(string ndjson)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ndjson));
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var row in NdjsonRowReader.ReadRowsAsync(stream, onRow: null, CancellationToken.None))
        {
            rows.Add(row);
        }

        return rows;
    }

    [Test]
    public async Task ReadRowsAsync_ExportedRawRow_YieldsClrValues()
    {
        var rows = await ReadAsync(
            """{"rtid":"6789a00000000000010011a3","timestamp":"2026-07-07T13:40:39.583Z","cktypeid":"EnergyIQ/CO2Sensor","rtcreationdatetime":"2026-07-07T13:40:41.431Z","rtchangeddatetime":"2026-07-07T13:40:41.431Z","rtwellknownname":null,"currentvalue":0}""" +
            "\n");

        await Assert.That(rows).Count().IsEqualTo(1);
        var row = rows[0];

        await Assert.That(row["rtid"]).IsEqualTo("6789a00000000000010011a3");
        await Assert.That(row["cktypeid"]).IsEqualTo("EnergyIQ/CO2Sensor");
        await Assert.That(row["timestamp"])
            .IsEqualTo(new DateTime(2026, 7, 7, 13, 40, 39, 583, DateTimeKind.Utc));
        await Assert.That(row["rtcreationdatetime"])
            .IsEqualTo(new DateTime(2026, 7, 7, 13, 40, 41, 431, DateTimeKind.Utc));
        await Assert.That(row["rtwellknownname"]).IsNull();
        await Assert.That(row["currentvalue"]).IsEqualTo(0L);
    }

    [Test]
    public async Task ReadRowsAsync_WindowedRow_ParsesWindowBoundsAsUtcDateTimes()
    {
        var rows = await ReadAsync(
            """{"rtid":"6789a00000000000010011a3","window_start":"2026-07-01T00:00:00Z","window_end":"2026-07-01T00:05:00Z","cktypeid":"EnergyIQ/Meter","activepower_avg_sum":12.5,"activepower_avg_count":3}""" +
            "\n");

        var row = rows[0];
        await Assert.That(row["window_start"]).IsEqualTo(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        await Assert.That(row["window_end"]).IsEqualTo(new DateTime(2026, 7, 1, 0, 5, 0, DateTimeKind.Utc));
        await Assert.That(row["activepower_avg_sum"]).IsEqualTo(12.5d);
        await Assert.That(row["activepower_avg_count"]).IsEqualTo(3L);
    }

    [Test]
    public async Task ReadRowsAsync_BooleansAndNonTimestampStrings_KeepTheirShape()
    {
        var rows = await ReadAsync(
            """{"rtid":"6789a00000000000010011a3","timestamp":"2026-07-07T13:40:39.583Z","cktypeid":"T","ison":true,"note":"2026-07-07 looks like a date but is a user string"}""" +
            "\n");

        var row = rows[0];
        await Assert.That((bool)row["ison"]!).IsTrue();
        await Assert.That(row["note"]).IsEqualTo("2026-07-07 looks like a date but is a user string");
    }

    [Test]
    public async Task ReadRowsAsync_BlankLines_AreSkippedAndRowsCounted()
    {
        var count = 0;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"rtid":"6789a00000000000010011a3","timestamp":"2026-07-07T13:40:39.583Z","cktypeid":"T","v":1}""" +
            "\n\n" +
            """{"rtid":"6789a00000000000010011a4","timestamp":"2026-07-07T13:41:39.583Z","cktypeid":"T","v":2}""" +
            "\n"));

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var row in NdjsonRowReader.ReadRowsAsync(stream, () => count++, CancellationToken.None))
        {
            rows.Add(row);
        }

        await Assert.That(rows).Count().IsEqualTo(2);
        await Assert.That(count).IsEqualTo(2);
    }
}
