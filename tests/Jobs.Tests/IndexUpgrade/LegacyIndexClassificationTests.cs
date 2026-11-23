using Meshmakers.Octo.Backend.Jobs.IndexUpgrade;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using MongoDB.Bson;

namespace Meshmakers.Octo.Backend.Jobs.Tests.IndexUpgrade;

/// <summary>
///     AB#9322 — every legacy index in exactly one category, decided by the collection roots of the tenant's CK metadata
///     and the state of their models (never by the index name); the record and warning forms; the mapping of the
///     engine's <c>CkType</c> documents.
/// </summary>
public class LegacyIndexClassificationTests
{
    [Test]
    public async Task CollectionRootOfAnAvailableModel_IsManaged()
    {
        var result = LegacyIndexClassification.Classify([Index("RtEntity_BasicAsset", "BasicAsset_1")],
            [Root("RtEntity_BasicAsset", "Basic-2.4.0", ModelState.Available)]);

        await Assert.That(result.Managed).Count().IsEqualTo(1);
        await Assert.That(result.NotAvailable).IsEmpty();
        await Assert.That(result.Orphaned).IsEmpty();
        await Assert.That(result.HasManaged).IsTrue();
        await Assert.That(result.HasLeftovers).IsFalse();
        await Assert.That(result.Warning()).IsNull();
    }

    [Test]
    [Arguments(ModelState.ResolveFailed)]
    [Arguments(ModelState.Importing)]
    public async Task CollectionRootOfAModelInAnotherState_IsModelNotAvailable_WithModelAndState(ModelState state)
    {
        var result = LegacyIndexClassification.Classify(
            [Index("RtEntity_MonitoringMonitoredTenant", "SystemEntity_0")],
            [Root("RtEntity_MonitoringMonitoredTenant", "Monitoring-2.1.0", state)]);

        await Assert.That(result.NotAvailable).Count().IsEqualTo(1);
        await Assert.That(result.NotAvailable[0].ModelId).IsEqualTo("Monitoring-2.1.0");
        await Assert.That(result.NotAvailable[0].State).IsEqualTo(state);
        await Assert.That(result.Managed).IsEmpty();
        await Assert.That(result.Orphaned).IsEmpty();
        await Assert.That(result.Warning()).Contains($"Monitoring-2.1.0 {state} (1 index)");
    }

    [Test]
    public async Task CollectionWithoutARoot_IsOrphaned_WhateverItsName()
    {
        // The name looks like a System collection; only the missing root decides.
        var result = LegacyIndexClassification.Classify(
            [Index("RtEntity_SystemQuery", "SystemQuery_1"), Index("Custom", "Custom_1")],
            [Root("RtEntity_SystemQueryV2", "System-2.8.0", ModelState.Available)]);

        await Assert.That(result.Orphaned.Select(i => i.CollectionName))
            .IsEquivalentTo(new[] { "RtEntity_SystemQuery", "Custom" });
        await Assert.That(result.Managed).IsEmpty();
        await Assert.That(result.NotAvailable).IsEmpty();
    }

    [Test]
    public async Task TwoRootsOnOneCollection_AnAvailableOneMakesItManaged()
    {
        var result = LegacyIndexClassification.Classify([Index("RtEntity_X", "X_1")],
        [
            Root("RtEntity_X", "Old-1.0.0", ModelState.ResolveFailed),
            Root("RtEntity_X", "New-2.0.0", ModelState.Available)
        ]);

        await Assert.That(result.Managed).Count().IsEqualTo(1);
        await Assert.That(result.NotAvailable).IsEmpty();
    }

    [Test]
    public async Task TheTwoTest2Shapes_AndAManagedIndex_EachIndexInExactlyOneCategory()
    {
        LegacyIndex[] legacy =
        [
            Index("RtEntity_MonitoringCkHealthMonitorConfig", "SystemEntity_0"),
            Index("RtEntity_MonitoringCkHealthMonitorConfig", "SystemEntity_2"),
            Index("RtEntity_MonitoringMonitoredTenant", "SystemEntity_0"),
            Index("RtEntity_MonitoringMonitoredTenant", "SystemEntity_2"),
            Index("RtEntity_SystemQuery", "SystemQuery_1"),
            Index("RtEntity_SystemQuery", "SystemQuery_2"),
            Index("RtEntity_BasicAsset", "BasicAsset_1")
        ];
        CkCollectionRoot[] roots =
        [
            Root("RtEntity_MonitoringCkHealthMonitorConfig", "Monitoring-2.1.0", ModelState.ResolveFailed),
            Root("RtEntity_MonitoringMonitoredTenant", "Monitoring-2.1.0", ModelState.ResolveFailed),
            Root("RtEntity_BasicAsset", "Basic-2.4.0", ModelState.Available)
        ];

        var result = LegacyIndexClassification.Classify(legacy, roots);
        var record = result.ToRecord();

        await Assert.That(result.Managed.Count + result.NotAvailable.Count + result.Orphaned.Count)
            .IsEqualTo(legacy.Length);
        await Assert.That((record.ManagedIndexes, record.ModelNotAvailableIndexes, record.OrphanedIndexes))
            .IsEqualTo((1, 4, 2));
        await Assert.That(record.ModelsNotAvailable).Count().IsEqualTo(1);
        await Assert.That((record.ModelsNotAvailable[0].ModelId, record.ModelsNotAvailable[0].State,
            record.ModelsNotAvailable[0].IndexCount)).IsEqualTo(("Monitoring-2.1.0", "ResolveFailed", 4));
        await Assert.That(record.OrphanedCollections).IsEquivalentTo(new[] { "RtEntity_SystemQuery" });
        await Assert.That(record.OrphanedCollectionsTotal).IsEqualTo(1);
        await Assert.That(result.Warning()).IsEqualTo(
            "6 legacy index(es) are left that the job cannot upgrade; model not Available: Monitoring-2.1.0 " +
            "ResolveFailed (4 indexes); orphaned (no collection root): RtEntity_SystemQuery (2 indexes)");
    }

    [Test]
    public async Task ManyOrphanedCollections_TheListIsCapped_TheTotalCountsAll()
    {
        var legacy = Enumerable.Range(1, 25).Select(n => Index($"RtEntity_Gone{n:D2}", "Gone_1")).ToList();

        var result = LegacyIndexClassification.Classify(legacy, []);
        var record = result.ToRecord();

        await Assert.That(record.OrphanedCollections).Count().IsEqualTo(LegacyIndexClassification.OrphanedCollectionsCap);
        await Assert.That(record.OrphanedCollections[0]).IsEqualTo("RtEntity_Gone01");
        await Assert.That(record.OrphanedCollectionsTotal).IsEqualTo(25);
        await Assert.That(result.Warning()).EndsWith("RtEntity_Gone20 (1 index) and 5 more collection(s)");
    }

    [Test]
    [Arguments("Monitoring-2.1.0/MonitoredTenant-1", "RtEntity_MonitoringMonitoredTenant")]
    [Arguments("System-1.0.3/Query-1", "RtEntity_SystemQuery")]
    [Arguments("System.Reporting-2.0.0/FileSystemEntity-1", "RtEntity_SystemReportingFileSystemEntity")]
    public async Task CollectionName_IsDerivedAsTheEngineDoesIt(string ckTypeId, string collection)
    {
        await Assert.That(CkCollectionRootReader.CollectionNameOf(ckTypeId)).IsEqualTo(collection);
    }

    [Test]
    public async Task CkTypeDocument_IsMapped_WithANumericOrTextState()
    {
        var numeric = CkCollectionRootReader.ToRoot(new BsonDocument
            { { "_id", "Monitoring-2.1.0/MonitoredTenant-1" }, { "ckModelId", "Monitoring-2.1.0" }, { "modelState", 2 } });
        var text = CkCollectionRootReader.ToRoot(new BsonDocument
            { { "_id", "Basic-2.4.0/Asset-1" }, { "ckModelId", "Basic-2.4.0" }, { "modelState", "Available" } });

        await Assert.That(numeric).IsEqualTo(new CkCollectionRoot("RtEntity_MonitoringMonitoredTenant",
            "Monitoring-2.1.0/MonitoredTenant-1", "Monitoring-2.1.0", ModelState.ResolveFailed));
        await Assert.That(text.State).IsEqualTo(ModelState.Available);
        await Assert.That(text.CollectionName).IsEqualTo("RtEntity_BasicAsset");
    }

    [Test]
    [Arguments("{ _id: 'A-1.0.0/T-1', ckModelId: 'A-1.0.0' }")]
    [Arguments("{ _id: 'A-1.0.0/T-1', ckModelId: 'A-1.0.0', modelState: 7 }")]
    [Arguments("{ _id: 'A-1.0.0/T-1', ckModelId: 'A-1.0.0', modelState: 'Gone' }")]
    [Arguments("{ _id: 'A-1.0.0/T-1', ckModelId: 'A-1.0.0', modelState: '7' }")]
    public async Task CkTypeDocument_WithoutAKnownState_FailsTheRead_NoGuess(string json)
    {
        await Assert.That(() => CkCollectionRootReader.ToRoot(BsonDocument.Parse(json))).Throws<FormatException>();
    }

    private static LegacyIndex Index(string collection, string name) =>
        new(collection, name, ["ckTypeId", "attributes.name", "rtState"]);

    private static CkCollectionRoot Root(string collection, string modelId, ModelState state) =>
        new(collection, $"{modelId}/{collection}-1", modelId, state);
}
