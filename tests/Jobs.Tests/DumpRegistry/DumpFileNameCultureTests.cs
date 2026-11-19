using System.Globalization;
using Meshmakers.Octo.Backend.Jobs.DumpRegistry;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meshmakers.Octo.Backend.Jobs.Tests.DumpRegistry;

[NotInParallel]
public class DumpFileNameCultureTests
{
    [Test]
    [Arguments("th-TH")]
    [Arguments("ar-SA")]
    [Arguments("de-AT")]
    public async Task DumpFileName_CarriesTheGregorianUtcTime_InEveryCulture(string cultureName)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(cultureName);
        try
        {
            var storage = new BackupFileStorageService(Path.GetTempPath(), Path.GetTempPath(),
                NullLogger<BackupFileStorageService>.Instance);
            var beforeNamingUtc = DateTime.UtcNow.AddSeconds(-1);

            var fileName = storage.GenerateDumpFileName("tenant-1");

            await Assert.That(DumpFileStartTime.TryRead(fileName, out var startUtc)).IsTrue();
            await Assert.That(startUtc).IsGreaterThanOrEqualTo(beforeNamingUtc.AddSeconds(-1));
            await Assert.That(startUtc).IsLessThanOrEqualTo(DateTime.UtcNow);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Test]
    [Arguments("th-TH")]
    [Arguments("ar-SA")]
    public async Task ArchiveContainerName_CarriesTheGregorianYear_InEveryCulture(string cultureName)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(cultureName);
        try
        {
            var fileName = DumpRepositoryJob.BuildBackupArchiveFileName("tenant-1");

            var gregorianYear = DateTime.UtcNow.Year.ToString(CultureInfo.InvariantCulture);
            await Assert.That(fileName).StartsWith($"tenant-1-{gregorianYear}");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
