using System.Text.Json;
using Meshmakers.Octo.Backend.BotServices.Controllers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Api;

/// <summary>
///     AB#6969 — a restore with failed archives or rollups that were not checked reports <c>SucceededWithWarnings</c>
///     in the job the CLI polls, while the job status stays <c>Succeeded</c>; other job results are left alone.
/// </summary>
public class RestoreOutcomeTests
{
    private static JobDto Apply(string? resultJson)
    {
        var dto = new JobDto { Id = "1", Status = "Succeeded" };
        RestoreOutcome.Apply(dto, resultJson);
        return dto;
    }

    /// <summary>
    ///     The result of a real restore job as Hangfire stored it (smoke run 2026-10-18, a format 2 container whose daily rollup
    ///     file was corrupted, archive ecaa...a03 failed): the shape the mapper has to read, key casing included.
    /// </summary>
    private const string CapturedFailedArchiveResult = """
        {"TenantId":"","DatabaseName":"","SecretSweep":{"TenantId":"smoke5968","Mode":1,"Trigger":2,"Outcome":0,"Reason":null,
         "Steps":[{"Mode":0,"Success":true,"Totals":{"NotSet":0,"Total":0},"Slots":[],"Cleared":[],"Unreadable":[],"Failures":[]}]},
         "BackupFormatVersion":2,
         "Archives":[
          {"RtId":"ecaa00000000000000000a01","Outcome":"Imported","Rows":864,"Seconds":1.2,"Detail":"status Activated"},
          {"RtId":"ecaa00000000000000000a02","Outcome":"Imported","Rows":216,"Seconds":0.8,"Detail":"status Disabled"},
          {"RtId":"ecaa00000000000000000a03","Outcome":"Failed","Rows":0,"Seconds":0.5,"Detail":"0 rows were loaded, 9 were expected."}],
         "RollupReconcile":{"Ok":false,"RepairRan":true,"FailedStep":"1 archive(s) of the backup failed to restore",
          "ResetRecomputeFlags":[],
          "Rollups":[{"RollupRtId":"ecaa00000000000000000a03","Before":["watermark ahead of rows"],"Repair":["RewindWatermark [Done]"],"After":[]}],
          "FailedArchives":[{"ArchiveRtId":"ecaa00000000000000000a03","Reason":"0 rows were loaded, 9 were expected."}],
          "NotCheckedRollups":[{"RollupRtId":"ecaa00000000000000000a03","Cause":"ArchiveFailed",
           "Reason":"the archive failed to restore, so its rows were not checked","IsBlocking":true}]}}
        """;

    private const string CapturedCleanResult = """
        {"TenantId":"","DatabaseName":"","SecretSweep":{"TenantId":"smoke5967","Mode":1,"Trigger":2,"Outcome":0,"Reason":null,"Steps":[]},
         "BackupFormatVersion":2,
         "Archives":[
          {"RtId":"ecab00000000000000000a01","Outcome":"Imported","Rows":864,"Seconds":1.1,"Detail":"status Activated"},
          {"RtId":"ecab00000000000000000a02","Outcome":"Imported","Rows":216,"Seconds":0.7,"Detail":"status Disabled"},
          {"RtId":"ecab00000000000000000a03","Outcome":"Imported","Rows":9,"Seconds":0.4,"Detail":"status Activated"}],
         "RollupReconcile":{"Ok":true,"RepairRan":false,"FailedStep":null,"ResetRecomputeFlags":[],
          "Rollups":[{"RollupRtId":"ecab00000000000000000a02","Before":[],"Repair":[],"After":[]},
                     {"RollupRtId":"ecab00000000000000000a03","Before":[],"Repair":[],"After":[]}],
          "FailedArchives":[],"NotCheckedRollups":[]}}
        """;

    /// <summary>
    ///     The wire round trip of the job the CLI polls: serialized the way the controller does (ASP.NET Core web defaults),
    ///     deserialized the way the SDK client does, so the field names agree on both sides.
    /// </summary>
    private static JobDto OverTheWire(string resultJson)
    {
        var json = JsonSerializer.Serialize(Apply(resultJson), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return JsonSerializer.Deserialize<JobDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    [Test]
    public async Task CapturedResultWithAFailedArchive_SurvivesTheWire_AsOutcomeFailedArchiveAndBlockingGap()
    {
        var dto = OverTheWire(CapturedFailedArchiveResult);

        await Assert.That(dto.Status).IsEqualTo("Succeeded");
        await Assert.That(dto.Outcome).IsEqualTo("SucceededWithWarnings");
        await Assert.That(dto.HasWarnings).IsTrue();
        await Assert.That(dto.FailedArchives!.Single().ArchiveRtId).IsEqualTo("ecaa00000000000000000a03");
        await Assert.That(dto.FailedArchives!.Single().Reason).IsEqualTo("0 rows were loaded, 9 were expected.");
        var gap = dto.NotCheckedRollups!.Single();
        await Assert.That(gap.RollupRtId).IsEqualTo("ecaa00000000000000000a03");
        await Assert.That(gap.Cause).IsEqualTo("ArchiveFailed");
        await Assert.That(gap.IsBlocking).IsTrue();
    }

    [Test]
    public async Task CapturedCleanResult_SurvivesTheWire_AsSucceededWithoutWarnings()
    {
        var dto = OverTheWire(CapturedCleanResult);

        await Assert.That(dto.Outcome).IsEqualTo("Succeeded");
        await Assert.That(dto.HasWarnings).IsFalse();
        await Assert.That(dto.FailedArchives!.Count).IsEqualTo(0);
        await Assert.That(dto.NotCheckedRollups!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ARealStoredRestoreResult_WithAFailedArchive_IsReadAsSucceededWithWarnings()
    {
        var dto = Apply(CapturedFailedArchiveResult);

        await Assert.That(dto.Status).IsEqualTo("Succeeded");
        await Assert.That(dto.Outcome).IsEqualTo("SucceededWithWarnings");
        await Assert.That(dto.FailedArchives!.Single().ArchiveRtId).IsEqualTo("ecaa00000000000000000a03");
        await Assert.That(dto.NotCheckedRollups!.Single().Cause).IsEqualTo("ArchiveFailed");
        await Assert.That(dto.NotCheckedRollups!.Single().IsBlocking).IsTrue();
    }

    [Test]
    public async Task AFailedArchive_IsListed_AndTheOutcomeNeedsAttention()
    {
        var dto = Apply("""
            {"BackupFormatVersion":2,"Archives":[],"RollupReconcile":{"Ok":false,"FailedStep":"1 archive(s) failed",
             "FailedArchives":[{"ArchiveRtId":"ec01","Reason":"table is gone"}],
             "NotCheckedRollups":[{"RollupRtId":"ec01","Cause":"ArchiveFailed","Reason":"not checked","IsBlocking":true}]}}
            """);

        await Assert.That(dto.Status).IsEqualTo("Succeeded");
        await Assert.That(dto.Outcome).IsEqualTo("SucceededWithWarnings");
        await Assert.That(dto.HasWarnings).IsTrue();
        await Assert.That(dto.FailedArchives!.Single().ArchiveRtId).IsEqualTo("ec01");
        await Assert.That(dto.FailedArchives!.Single().Reason).IsEqualTo("table is gone");
        await Assert.That(dto.NotCheckedRollups!.Single().Cause).IsEqualTo("ArchiveFailed");
        await Assert.That(dto.NotCheckedRollups!.Single().IsBlocking).IsTrue();
    }

    [Test]
    public async Task AFailedArchive_WithoutAReconcile_StillNeedsAttention_AndIsListedOnce()
    {
        var dto = Apply("""
            {"BackupFormatVersion":2,"Archives":[
             {"RtId":"ec01","Outcome":"Imported","Rows":5,"Detail":"status Activated"},
             {"RtId":"ec02","Outcome":"Failed","Rows":0,"Detail":"0 rows were loaded, 9 were expected."}],
             "RollupReconcile":null}
            """);

        await Assert.That(dto.Status).IsEqualTo("Succeeded");
        await Assert.That(dto.Outcome).IsEqualTo("SucceededWithWarnings");
        await Assert.That(dto.HasWarnings).IsTrue();
        await Assert.That(dto.FailedArchives!.Single().ArchiveRtId).IsEqualTo("ec02");
        await Assert.That(dto.FailedArchives!.Single().Reason).IsEqualTo("0 rows were loaded, 9 were expected.");
    }

    [Test]
    public async Task AFailedArchive_InBothTheArchivesAndTheReconcile_IsListedOnce()
    {
        var dto = Apply("""
            {"BackupFormatVersion":2,"Archives":[{"RtId":"ec01","Outcome":"Failed","Detail":"archive detail"}],
             "RollupReconcile":{"Ok":false,"FailedArchives":[{"ArchiveRtId":"ec01","Reason":"reconcile reason"}]}}
            """);

        await Assert.That(dto.FailedArchives!.Count).IsEqualTo(1);
        await Assert.That(dto.FailedArchives!.Single().Reason).IsEqualTo("reconcile reason");
    }

    [Test]
    public async Task ACleanRestore_ReportsSucceeded()
    {
        var dto = Apply("""{"BackupFormatVersion":2,"Archives":[],"RollupReconcile":{"Ok":true,"FailedArchives":[],"NotCheckedRollups":[]}}""");

        await Assert.That(dto.Outcome).IsEqualTo("Succeeded");
        await Assert.That(dto.HasWarnings).IsFalse();
    }

    [Test]
    public async Task AnInformationalGap_DoesNotNeedAttention()
    {
        var dto = Apply("""
            {"BackupFormatVersion":1,"RollupReconcile":{"Ok":true,"NotCheckedRollups":[
             {"RollupRtId":"ec02","Cause":"NeverActivated","Reason":"never activated","IsBlocking":false}]}}
            """);

        await Assert.That(dto.Outcome).IsEqualTo("Succeeded");
        await Assert.That(dto.NotCheckedRollups!.Single().IsBlocking).IsFalse();
        await Assert.That(dto.HasWarnings).IsFalse();
    }

    [Test]
    public async Task AReconcileThatFailed_NeedsAttentionEvenWithoutAFailedArchive()
    {
        var dto = Apply("""{"BackupFormatVersion":2,"RollupReconcile":{"Ok":false,"FailedStep":"repair failed"}}""");

        await Assert.That(dto.Outcome).IsEqualTo("SucceededWithWarnings");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("\"tenant-dumps/t/file.octobak.zip\"")]
    [Arguments("{\"unrelated\":1}")]
    [Arguments("not json {")]
    public async Task OtherJobResults_AreLeftAlone(string? result)
    {
        var dto = Apply(result);

        await Assert.That(dto.Outcome).IsNull();
        await Assert.That(dto.FailedArchives).IsNull();
        await Assert.That(dto.NotCheckedRollups).IsNull();
    }
}
