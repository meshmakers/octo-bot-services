using Hangfire;
using Hangfire.Storage;
using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Secrets;

/// <summary>
///     AB#5544 — sweep run history per tenant on the Hangfire job storage.
/// </summary>
public class HangfireSecretSweepRunStoreTests
{
    private readonly IStorageConnection _connection = Substitute.For<IStorageConnection>();
    private readonly Dictionary<string, Dictionary<string, string>> _hashes = new();
    private readonly HangfireSecretSweepRunStore _store;

    public HangfireSecretSweepRunStoreTests()
    {
        var transaction = Substitute.For<IWriteOnlyTransaction>();
        _connection.CreateWriteTransaction().Returns(transaction);
        _connection.AcquireDistributedLock(Arg.Any<string>(), Arg.Any<TimeSpan>()).Returns(Substitute.For<IDisposable>());
        transaction.When(t => t.SetRangeInHash(Arg.Any<string>(), Arg.Any<IEnumerable<KeyValuePair<string, string>>>()))
            .Do(ci => _hashes[ci.ArgAt<string>(0)] = ci.ArgAt<IEnumerable<KeyValuePair<string, string>>>(1)
                .ToDictionary(kv => kv.Key, kv => kv.Value));
        _connection.GetAllEntriesFromHash(Arg.Any<string>())
            .Returns(ci => _hashes.TryGetValue(ci.Arg<string>(), out var hash) ? hash : null);

        var storage = Substitute.For<JobStorage>();
        storage.GetConnection().Returns(_connection);
        _store = new HangfireSecretSweepRunStore(() => storage);
    }

    private static SecretSweepRunDto Run(string id, SecretSweepOutcomeDto outcome = SecretSweepOutcomeDto.Running)
    {
        return new SecretSweepRunDto { RunId = id, Mode = SecretSweepModeDto.Encrypt, Outcome = outcome };
    }

    [Test]
    public async Task Upsert_InsertsNewestFirst_AndReplacesInPlace_CaseInsensitiveTenant()
    {
        await _store.UpsertAsync("Tenant", Run("a"));
        await _store.UpsertAsync("tenant", Run("b"));
        await _store.UpsertAsync("TENANT", Run("a", SecretSweepOutcomeDto.Succeeded));

        var runs = await _store.GetRunsAsync("tenant");

        await Assert.That(runs.Select(r => r.RunId).ToArray()).IsEquivalentTo(new[] { "b", "a" });
        await Assert.That(runs[0].RunId).IsEqualTo("b");
        await Assert.That(runs[1].Outcome).IsEqualTo(SecretSweepOutcomeDto.Succeeded);
        // Enum names in the stored JSON, under the per-tenant lock.
        await Assert.That(_hashes[HangfireSecretSweepRunStore.RunsKey("tenant")][HangfireSecretSweepRunStore.RunsField])
            .Contains("\"Succeeded\"");
        _connection.Received(3).AcquireDistributedLock(HangfireSecretSweepRunStore.LockPrefix + "tenant",
            HangfireSecretSweepRunStore.LockTimeout);
    }

    [Test]
    public async Task Upsert_KeepsTheLast50()
    {
        for (var i = 0; i < 55; i++)
        {
            await _store.UpsertAsync("t", Run($"r{i}"));
        }

        var runs = await _store.GetRunsAsync("t", 100);

        await Assert.That(runs.Count).IsEqualTo(50);
        await Assert.That(runs[0].RunId).IsEqualTo("r54");
        await Assert.That(runs[^1].RunId).IsEqualTo("r5");
    }

    [Test]
    public async Task GetRuns_HonoursTheLimit_AndUnknownTenantIsEmpty()
    {
        await _store.UpsertAsync("t", Run("a"));
        await _store.UpsertAsync("t", Run("b"));

        await Assert.That((await _store.GetRunsAsync("t", 1)).Single().RunId).IsEqualTo("b");
        await Assert.That(await _store.GetRunsAsync("nobody")).IsEmpty();
    }

    [Test]
    public async Task Update_ChangesAndStores_OnlyWhenTheCallbackSaysSo()
    {
        await _store.UpsertAsync("t", Run("a"));

        var unknown = await _store.UpdateAsync("t", "zzz", _ => true);
        await _store.UpdateAsync("t", "a", r =>
        {
            r.TriggeredBy = "not stored";
            return false;
        });
        var updated = await _store.UpdateAsync("t", "a", r =>
        {
            r.TriggeredBy = "alice";
            return true;
        });

        await Assert.That(unknown).IsNull();
        await Assert.That(updated!.TriggeredBy).IsEqualTo("alice");
        await Assert.That((await _store.GetRunsAsync("t")).Single().TriggeredBy).IsEqualTo("alice");
    }

    [Test]
    public async Task MarkDumpDeleted_SetsDeletedAtOnce_ForTheRunWithThatFile()
    {
        var at = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var withDump = Run("a");
        withDump.Dump = new SecretSweepDumpDto { FileName = "f.presweep.tar.gz", Exists = true };
        await _store.UpsertAsync("t", withDump);
        await _store.UpsertAsync("t", Run("b"));

        var first = await _store.MarkDumpDeletedAsync("t", "f.presweep.tar.gz", at, null);
        var second = await _store.MarkDumpDeletedAsync("t", "f.presweep.tar.gz", at.AddHours(1), "x");
        var other = await _store.MarkDumpDeletedAsync("t", "other.presweep.tar.gz", at, null);

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsFalse();
        await Assert.That(other).IsFalse();
        var dump = (await _store.GetRunsAsync("t")).Single(r => r.RunId == "a").Dump!;
        await Assert.That(dump.DeletedAt).IsEqualTo(at);
        await Assert.That(dump.DeletedBy).IsNull();
        await Assert.That(dump.Exists).IsFalse();
    }

    [Test]
    public async Task Upsert_ReplacingARunWhoseDumpWasDeleted_KeepsTheDeletion()
    {
        // The sweep writes its run again (outcome) after a dump DELETE: the deletion must not be lost.
        var at = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var run = Run("a");
        run.Dump = new SecretSweepDumpDto { FileName = "f.presweep.tar.gz", Exists = true };
        await _store.UpsertAsync("t", run);
        await _store.MarkDumpDeletedAsync("t", "f.presweep.tar.gz", at, "alice");

        var final = Run("a", SecretSweepOutcomeDto.Succeeded);
        final.Dump = new SecretSweepDumpDto { FileName = "f.presweep.tar.gz", Exists = true };
        await _store.UpsertAsync("t", final);

        var stored = (await _store.GetRunsAsync("t")).Single();
        await Assert.That(stored.Outcome).IsEqualTo(SecretSweepOutcomeDto.Succeeded);
        await Assert.That(stored.Dump!.DeletedAt).IsEqualTo(at);
        await Assert.That(stored.Dump.DeletedBy).IsEqualTo("alice");
        await Assert.That(stored.Dump.Exists).IsFalse();
    }
}
