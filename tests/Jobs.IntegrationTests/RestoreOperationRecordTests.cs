using Meshmakers.Octo.Backend.BotServices;
using Meshmakers.Octo.Backend.BotServices.Consumers;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Methods.MongoDb;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.IntegrationTests;

/// <summary>
///     AB#8447 (F3.6), against a real MongoDB replica set: the operation record of a restore lives in the system database
///     (<c>MethodOperations</c>), so it stays readable while and after the tenant database is replaced (dropped and
///     restored, which is what <c>mongorestore --drop</c> does to every collection) — including the <c>PosDeleteTenant</c>
///     the restore sends when it drops and re-attaches the tenant; and a deleted tenant (Deleting tombstone) takes its
///     records with it (<c>PosDeleteTenant</c> through the real <see cref="TenantManagementConsumer" /> and the real
///     MongoDB store), while another tenant keeps its own.
/// </summary>
[NotInParallel]
public class RestoreOperationRecordTests
{
    [Test]
    public async Task RestoreRecord_SurvivesTheReplacedTenantDatabase_AndADeletedTenantLosesItsRecords()
    {
        var fixture = await ExportEngineFixture.GetAsync();
        var system = fixture.Provider.GetRequiredService<IOptions<OctoSystemConfiguration>>().Value;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var tenantId = $"restored{suffix}";
        var otherTenantId = $"other{suffix}";
        var systemDatabase = $"opsystem{suffix}";

        // The store on its own system database (the fixture's engine uses its tenant database as system database).
        var store = new CkMethodMongoOperationStore(Options.Create(new OctoSystemConfiguration
            {
                SystemTenantId = systemDatabase,
                SystemDatabaseName = systemDatabase,
                DatabaseHost = system.DatabaseHost,
                AdminUser = system.AdminUser,
                AdminUserPassword = system.AdminUserPassword,
                DatabaseUserPassword = system.DatabaseUserPassword,
                UseDirectConnection = system.UseDirectConnection
            }), TimeProvider.System, NullLogger<CkMethodMongoOperationStore>.Instance);
        using var disposeStore = store;
        var client = new MongoClient(
            $"mongodb://{system.AdminUser}:{system.AdminUserPassword}@{system.DatabaseHost}/?directConnection=true");

        // A tenant database with data, and a running restore operation of that tenant.
        var tenantDatabase = client.GetDatabase(tenantId);
        await tenantDatabase.GetCollection<BsonDocument>("RtEntities").InsertOneAsync(new BsonDocument("name", "before"));
        var restore = NewRecord(tenantId);
        var other = NewRecord(otherTenantId);
        await store.CreateAsync(restore);
        await store.CreateAsync(other);
        await store.MarkRunningAsync(tenantId, restore.OperationId, DateTime.UtcNow);

        // The bot service's consumer, as every replica runs it; the lifecycle record tells a delete from a restore.
        var lifecycle = Substitute.For<ITenantLifecycleStore>();
        var lifecycleRecord = new TenantLifecycleRecord { TenantId = tenantId, State = TenantLifecycleState.Active };
        lifecycle.GetAsync(tenantId, Arg.Any<CancellationToken>()).Returns(_ => lifecycleRecord);
        var consumer = new TenantManagementConsumer(NullLogger<TenantManagementConsumer>.Instance,
            Options.Create(new OctoBotServicesOptions()), new NoJobs(), store, lifecycle);
        var context = Substitute.For<IDistributedContext<PosDeleteTenant>>();
        context.Message.Returns(new PosDeleteTenant(tenantId, Guid.NewGuid(), DateTime.UtcNow));

        // The restore drops the tenant (PosDeleteTenant after the drop, no tombstone) and replaces the tenant database.
        await consumer.ConsumeAsync(context);
        await client.DropDatabaseAsync(tenantId);
        await tenantDatabase.GetCollection<BsonDocument>("RtEntities").InsertOneAsync(new BsonDocument("name", "after"));

        var during = await store.GetAsync(tenantId, restore.OperationId);
        await Assert.That(during).IsNotNull();
        await Assert.That(during!.State).IsEqualTo(CkMethodOperationState.Running);
        await Assert.That(await store.CompleteAsync(tenantId, restore.OperationId, new CkMethodOperationCompletion
        {
            State = CkMethodOperationState.Succeeded,
            ResultJson = "{\"jobId\":\"job-1\",\"archiveCount\":0,\"failedArchiveCount\":0,\"secretsToReEnterCount\":0}",
            CompletedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7)
        })).IsTrue();
        await Assert.That((await store.GetAsync(tenantId, restore.OperationId))!.State)
            .IsEqualTo(CkMethodOperationState.Succeeded);

        // The tenant is deleted (the delete writes the Deleting tombstone first): the consumer purges its records, the
        // other tenant keeps its own.
        lifecycleRecord = new TenantLifecycleRecord { TenantId = tenantId, State = TenantLifecycleState.Deleting };
        await consumer.ConsumeAsync(context);

        await Assert.That(await store.GetAsync(tenantId, restore.OperationId)).IsNull();
        await Assert.That(await store.GetAsync(otherTenantId, other.OperationId)).IsNotNull();

        await client.DropDatabaseAsync(tenantId);
        await client.DropDatabaseAsync(systemDatabase);
    }

    private static CkMethodOperationRecord NewRecord(string tenantId) => new()
    {
        TenantId = tenantId,
        OperationId = Guid.NewGuid().ToString("N"),
        MethodId = "System.Bot/TenantRepository.Restore-1",
        InvocationId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        Owner = new CkMethodOperationOwner("user-1", null),
        HandlerInstanceId = "bot-1",
        AcceptedAtUtc = DateTime.UtcNow,
        HeartbeatAtUtc = DateTime.UtcNow
    };

    private sealed class NoJobs : IJobCreatorService
    {
        public void CreateJobs(string instancePrefix, string tenantId)
        {
        }

        public void DeleteJobs(string instancePrefix, string tenantId)
        {
        }
    }
}
