using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs;

/// <summary>
///     The warnings of an engine restore or backup result: the output lines that name ClearCache, and a fallback when the
///     tenant-created notification failed without such a line.
/// </summary>
public class TenantNotificationWarningsTests
{
    private const string CreateWarning = "Tenant 't1' was restored and attached to database 'db1', but the " +
                                         "tenant-created notification failed (boom). The services have not set the " +
                                         "tenant up: run ClearCache for the tenant.";

    [Test]
    public async Task NoFlagAndNoClearCacheLine_HasNoWarnings()
    {
        var warnings = TenantNotificationWarnings.From(
            new CommandResult { Success = true, Output = "2026-11-17 done, 12 documents restored" }, "t1");

        await Assert.That(warnings).IsEmpty();
    }

    [Test]
    public async Task ClearCacheLines_AreTakenFromTheOutput_OtherLinesAreNot()
    {
        const string deleteWarning = "Tenant 't1' was dropped, but the tenant-deleted notification failed (boom). " +
                                     "The services may still hold the tenant's cached state: run ClearCache for the tenant.";
        var warnings = TenantNotificationWarnings.From(new CommandResult
        {
            Success = true,
            TenantCreatedNotificationFailed = true,
            Output = "12 documents restored\r\n" + CreateWarning + "\n" + deleteWarning + "\n"
        }, "t1");

        await Assert.That(warnings).IsEquivalentTo(new[] { CreateWarning, deleteWarning });
    }

    [Test]
    public async Task FlagWithoutALine_GetsAFallbackThatNamesClearCacheAndTheTenant()
    {
        var warnings = TenantNotificationWarnings.From(
            new CommandResult { Success = true, TenantCreatedNotificationFailed = true }, "t1");

        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0]).Contains("run ClearCache for the tenant");
        await Assert.That(warnings[0]).Contains("'t1'");
    }
}
