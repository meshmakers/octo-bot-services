using Meshmakers.Octo.Backend.BotServices.Methods;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Methods;

/// <summary>
///     AB#8881 (security): a tenant created again with the id of a deleted tenant never sees the deleted tenant's method
///     operation records in bot-services — not those a failed purge on delete left behind, not one a call admitted just
///     before the delete wrote after the purge. Reads ignore them (the store registration wraps the store in the generation
///     guard, generation = creation time of the tenant lifecycle record), and the tenant setup removes them.
/// </summary>
public class OperationRecordGenerationCleanupTests
{
    private const string Tenant = "reused";
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));
    private readonly ITenantLifecycleStore _lifecycle = Substitute.For<ITenantLifecycleStore>();
    private readonly CkMethodInMemoryOperationStore _inner;
    private readonly CkMethodTenantGenerationOperationStore _store;

    public OperationRecordGenerationCleanupTests()
    {
        _inner = new CkMethodInMemoryOperationStore(_time);
        _store = new CkMethodTenantGenerationOperationStore(_inner, new CkMethodTenantLifecycleGeneration(_lifecycle));
        TenantCreatedAt(Now - TimeSpan.FromDays(3));
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    [Test]
    public async Task FailedPurgeOnDelete_ThenRecreateSameId_TheNewTenantSeesNothing_AndSetupRemovesThem()
    {
        var finished = await Accept();
        await _store.CompleteAsync(Tenant, finished.OperationId, new CkMethodOperationCompletion
        {
            State = CkMethodOperationState.Succeeded, ResultJson = "{}", CompletedAtUtc = Now,
            ExpiresAtUtc = Now + Retention
        });
        var open = await Accept();

        // the delete's purge failed (nothing removed); the id is created again after the delete settled
        _time.Advance(TimeSpan.FromMinutes(3));
        TenantCreatedAt(Now);

        var gate = new DumpOperationGate(_store);
        await Assert.That(await gate.IsOperationOpenAsync(Tenant, open.OperationId, CancellationToken.None)).IsFalse();
        await Assert.That(await _store.GetAsync(Tenant, finished.OperationId)).IsNull();
        await Assert.That((await _store.ListAsync(new CkMethodOperationQuery { TenantId = Tenant })).TotalCount)
            .IsEqualTo(0);

        var removed = await Cleanup().RunAsync(Tenant);

        await Assert.That(removed).IsEqualTo(2);
        await Assert.That(_inner.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RecordWrittenAfterThePurge_BeforeRecreate_IsInvisible_AndTheNewTenantKeepsItsOwn()
    {
        var admittedAt = Now;
        _time.Advance(TimeSpan.FromSeconds(1));
        await _store.DeleteTenantAsync(Tenant); // the purge on delete succeeds
        _time.Advance(TimeSpan.FromSeconds(1));
        var late = await Accept(admittedAt); // a call admitted before the delete writes its record now
        _time.Advance(TimeSpan.FromSeconds(90));
        TenantCreatedAt(Now);
        _time.Advance(TimeSpan.FromSeconds(5));
        var own = await Accept();

        await Assert.That(await _store.GetAsync(Tenant, late.OperationId)).IsNull();
        var page = await _store.ListAsync(new CkMethodOperationQuery { TenantId = Tenant });
        await Assert.That(page.Items.Select(r => r.OperationId)).IsEquivalentTo(new[] { own.OperationId });

        await Assert.That(await Cleanup().RunAsync(Tenant)).IsEqualTo(1);
        await Assert.That(await _store.GetAsync(Tenant, own.OperationId)).IsNotNull();
    }

    [Test]
    public async Task RestoredTenant_KeepsItsCreationTime_SoTheRestoreRecordStays()
    {
        // a restore drops and re-attaches the database; the lifecycle record and its creation time stay
        var restore = await Accept();

        await Assert.That(await Cleanup().RunAsync(Tenant)).IsEqualTo(0);
        await Assert.That(await _store.GetAsync(Tenant, restore.OperationId)).IsNotNull();
    }

    [Test]
    public async Task UnknownGeneration_RemovesNothing()
    {
        await Accept();
        _lifecycle.GetAsync(Tenant, Arg.Any<CancellationToken>()).Returns((TenantLifecycleRecord?)null);

        await Assert.That(await Cleanup().RunAsync(Tenant)).IsEqualTo(0);
        await Assert.That(_inner.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Failure_IsLogged_NeverThrown()
    {
        _lifecycle.GetAsync(Tenant, Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("database down"));

        await Assert.That(await Cleanup().RunAsync(Tenant)).IsEqualTo(0);
    }

    private OperationRecordGenerationCleanup Cleanup() =>
        new(new CkMethodTenantLifecycleGeneration(_lifecycle), _store,
            NullLogger<OperationRecordGenerationCleanup>.Instance);

    private void TenantCreatedAt(DateTime createdUtc) =>
        _lifecycle.GetAsync(Tenant, Arg.Any<CancellationToken>()).Returns(new TenantLifecycleRecord
        {
            TenantId = Tenant, State = TenantLifecycleState.Active, CreatedUtc = createdUtc
        });

    private async Task<CkMethodOperationRecord> Accept(DateTime? acceptedAt = null)
    {
        var record = new CkMethodOperationRecord
        {
            TenantId = Tenant,
            OperationId = Guid.NewGuid().ToString("N"),
            MethodId = "System.Bot/TenantRepository.Dump-1",
            InvocationId = Guid.NewGuid(),
            Owner = new CkMethodOperationOwner("user-1", null),
            HandlerInstanceId = "bot-1",
            AcceptedAtUtc = acceptedAt ?? Now,
            HeartbeatAtUtc = acceptedAt ?? Now
        };
        await _store.CreateAsync(record);
        return record;
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
