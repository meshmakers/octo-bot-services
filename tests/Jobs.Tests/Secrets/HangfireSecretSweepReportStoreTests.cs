using Hangfire;
using Hangfire.Storage;
using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Secrets;

/// <summary>
///     AB#5539 — last report per tenant on the Hangfire job storage.
/// </summary>
public class HangfireSecretSweepReportStoreTests
{
    private readonly Dictionary<string, Dictionary<string, string>> _hashes = new();
    private readonly HashSet<string> _tenants = [];
    private readonly HangfireSecretSweepReportStore _store;

    public HangfireSecretSweepReportStoreTests()
    {
        var connection = Substitute.For<IStorageConnection>();
        var transaction = Substitute.For<IWriteOnlyTransaction>();
        connection.CreateWriteTransaction().Returns(transaction);
        transaction.When(t => t.SetRangeInHash(Arg.Any<string>(), Arg.Any<IEnumerable<KeyValuePair<string, string>>>()))
            .Do(ci => _hashes[ci.ArgAt<string>(0)] = ci.ArgAt<IEnumerable<KeyValuePair<string, string>>>(1)
                .ToDictionary(kv => kv.Key, kv => kv.Value));
        transaction.When(t => t.AddToSet(HangfireSecretSweepReportStore.TenantSetKey, Arg.Any<string>()))
            .Do(ci => _tenants.Add(ci.ArgAt<string>(1)));
        connection.GetAllEntriesFromHash(Arg.Any<string>())
            .Returns(ci => _hashes.TryGetValue(ci.Arg<string>(), out var hash) ? hash : null);
        connection.GetAllItemsFromSet(HangfireSecretSweepReportStore.TenantSetKey).Returns(_ => _tenants);

        var storage = Substitute.For<JobStorage>();
        storage.GetConnection().Returns(connection);
        _store = new HangfireSecretSweepReportStore(() => storage);
    }

    [Test]
    public async Task SaveThenGet_RoundTripsTheReport_CaseInsensitiveTenant()
    {
        var report = new SecretSweepReport
        {
            TenantId = "MyTenant",
            Mode = SecretSweepMode.Encrypt,
            Outcome = SecretSweepOutcome.CompletedWithFailures,
            Steps =
            [
                new SecretSweepStepReport
                {
                    Mode = SecretSweepMode.Encrypt,
                    Totals = new SecretFormCountsReport { Plaintext = 2, EncV2ByKeyId = { ["k1"] = 5 } }
                }
            ],
            SecretsToReEnter = [new SecretValueReference { CkTypeId = "T/Type", RtId = "r1", AttributePath = "Password" }]
        };

        await _store.SaveAsync(report);
        var read = await _store.GetLastAsync("mytenant");

        await Assert.That(read).IsNotNull();
        await Assert.That(read!.TenantId).IsEqualTo("MyTenant");
        await Assert.That(read.Outcome).IsEqualTo(SecretSweepOutcome.CompletedWithFailures);
        await Assert.That(read.Steps[0].Totals.EncV2ByKeyId["k1"]).IsEqualTo(5);
        await Assert.That(read.Steps[0].Totals.Legacy).IsEqualTo(2);
        await Assert.That(read.SecretsToReEnter[0].AttributePath).IsEqualTo("Password");
        // Enum names, not numbers, in the stored JSON.
        await Assert.That(_hashes.Values.Single()[HangfireSecretSweepReportStore.ReportField])
            .Contains("\"CompletedWithFailures\"");
    }

    [Test]
    public async Task Get_Unknown_IsNull()
    {
        await Assert.That(await _store.GetLastAsync("nobody")).IsNull();
    }

    [Test]
    public async Task GetAll_ReturnsTheLastReportOfEveryTenant_Ordered()
    {
        await _store.SaveAsync(new SecretSweepReport { TenantId = "b", Mode = SecretSweepMode.Verify });
        await _store.SaveAsync(new SecretSweepReport { TenantId = "a", Mode = SecretSweepMode.Verify });
        await _store.SaveAsync(new SecretSweepReport { TenantId = "a", Mode = SecretSweepMode.Encrypt });

        var all = await _store.GetAllLastAsync();

        await Assert.That(all.Select(r => r.TenantId).ToArray()).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(all[0].Mode).IsEqualTo(SecretSweepMode.Encrypt);
    }
}
