using System.Security.Cryptography;
using System.Text;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Services.ArtifactStorage;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Services;

/// <summary>
///     AB#5559 / AB#5561 — the bot's artifact storage over a real file system store and the engine's real file
///     protector with generated keys: encryption rules, round trips, tamper / key-loss handling, header-only reads.
/// </summary>
public class BotArtifactStorageTests : IDisposable
{
    private const string Tenant = "Tenant-A";
    private readonly ArtifactTestEnvironment _env = new("k1");

    public void Dispose()
    {
        _env.Dispose();
    }

    private static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(1024 * 1024)]
    [Arguments(1024 * 1024 + 17)]
    public async Task Required_EncryptsIntoTheStore_AndRoundTripsToAFile(int length)
    {
        var plaintext = Payload(length);
        var source = _env.WriteFile("dump.tar.gz", plaintext);

        var stored = await _env.Storage.StoreFileAsync(ArtifactCategories.Presweep, Tenant, "t-1.presweep", source,
            ArtifactEncryption.Required);

        await Assert.That(stored.FileName).IsEqualTo("t-1.presweep.octoenc");
        await Assert.That(stored.TenantId).IsEqualTo("tenant-a");
        await Assert.That(stored.Encrypted).IsTrue();
        var raw = await _env.ReadStoredAsync(ArtifactCategories.Presweep, Tenant, stored.FileName);
        await Assert.That(Encoding.ASCII.GetString(raw, 0, 8)).IsEqualTo(SecretFileFormat.Magic);
        await Assert.That(stored.Size).IsEqualTo(raw.LongLength);

        var target = Path.Combine(_env.Directory, "restored.tar.gz");
        var found = await _env.Storage.TryWritePlainToFileAsync(ArtifactCategories.Presweep, Tenant, stored.FileName,
            target);

        await Assert.That(found).IsTrue();
        await Assert.That((await File.ReadAllBytesAsync(target)).SequenceEqual(plaintext)).IsTrue();
        await Assert.That(File.Exists(source)).IsTrue();
    }

    [Test]
    public async Task Required_WithoutActiveKey_IsRefused_AndStoresNothing()
    {
        var storage = _env.CreateStorage(ArtifactTestEnvironment.CreateProtector(_env.Ring, null));
        var source = _env.WriteFile("dump.tar.gz", Payload(100));

        await Assert.That(async () => await storage.StoreFileAsync(ArtifactCategories.Presweep, Tenant, "x.presweep",
                source, ArtifactEncryption.Required))
            .Throws<SecretEncryptionNotConfiguredException>();

        var listed = new List<ArtifactInfo>();
        await foreach (var info in _env.Store.ListAsync(""))
        {
            listed.Add(info);
        }

        await Assert.That(listed).IsEmpty();
    }

    [Test]
    public async Task IfConfigured_WithoutKeyRing_StoresPlain_AndDownloadsUnchanged()
    {
        var storage = _env.CreateStorage(ArtifactTestEnvironment.CreateProtector(new Dictionary<string, byte[]>(), null));
        var plaintext = Payload(5000);
        var source = _env.WriteFile("t.tar.gz", plaintext);

        var stored = await storage.StoreFileAsync(ArtifactCategories.TenantDumps, Tenant, "t.tar.gz", source,
            ArtifactEncryption.IfConfigured);

        await Assert.That(stored.FileName).IsEqualTo("t.tar.gz");
        await Assert.That(stored.Encrypted).IsFalse();
        await using var download = await storage.OpenDownloadAsync(ArtifactCategories.TenantDumps, Tenant, "t.tar.gz");
        await Assert.That(download!.Encrypted).IsFalse();
        await Assert.That(download.PlainLength).IsEqualTo(5000);
        using var copy = new MemoryStream();
        await storage.CopyPlainAsync(download, copy);
        await Assert.That(copy.ToArray().SequenceEqual(plaintext)).IsTrue();
    }

    [Test]
    [Arguments(1)]
    [Arguments(1024 * 1024)]
    [Arguments(3 * 1024 * 1024 + 5)]
    public async Task Download_OfAnEncryptedArtifact_IsThePlainFile_WithItsPlainLength(int length)
    {
        var plaintext = Payload(length);
        var source = _env.WriteFile("t.octobak.zip", plaintext);
        var stored = await _env.Storage.StoreFileAsync(ArtifactCategories.TenantDumps, Tenant, "t.octobak.zip", source,
            ArtifactEncryption.IfConfigured);

        await using var download =
            await _env.Storage.OpenDownloadAsync(ArtifactCategories.TenantDumps, Tenant, stored.FileName);

        await Assert.That(download!.Encrypted).IsTrue();
        await Assert.That(download.DownloadFileName).IsEqualTo("t.octobak.zip");
        await Assert.That(download.PlainLength).IsEqualTo(length);
        await Assert.That(download.Header!.KeyId).IsEqualTo("k1");
        using var copy = new MemoryStream();
        await _env.Storage.CopyPlainAsync(download, copy);
        await Assert.That(copy.ToArray().SequenceEqual(plaintext)).IsTrue();
    }

    [Test]
    public async Task TamperedArtifact_FailsVerification_AndLeavesNoPlaintextBehind()
    {
        // Two full chunks verify before the tampered third one fails: their plaintext must not survive.
        var source = _env.WriteFile("dump", Payload(3 * 1024 * 1024));
        var stored = await _env.Storage.StoreFileAsync(ArtifactCategories.Presweep, Tenant, "t.presweep", source,
            ArtifactEncryption.Required);
        var path = Path.Combine(_env.StoreRoot,
            _env.Keys.Build(ArtifactCategories.Presweep, Tenant, stored.FileName).Replace('/', Path.DirectorySeparatorChar));
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[^100] ^= 0x5A;
        await File.WriteAllBytesAsync(path, bytes);
        var target = Path.Combine(_env.Directory, "restored");

        await Assert.That(async () => await _env.Storage.TryWritePlainToFileAsync(ArtifactCategories.Presweep, Tenant,
                stored.FileName, target))
            .Throws<InvalidSecretFileException>();
        await Assert.That(File.Exists(target)).IsFalse();
    }

    [Test]
    public async Task KeyRemovedFromTheRing_HeaderStillReadable_ButNotDecryptable()
    {
        var source = _env.WriteFile("dump", Payload(2048));
        var stored = await _env.Storage.StoreFileAsync(ArtifactCategories.Presweep, Tenant, "t.presweep", source,
            ArtifactEncryption.Required);
        var rotated = new Dictionary<string, byte[]> { ["k2"] = RandomNumberGenerator.GetBytes(32) };
        var storage = _env.CreateStorage(ArtifactTestEnvironment.CreateProtector(rotated, "k2"));

        var header = await storage.ReadHeaderAsync(ArtifactCategories.Presweep, Tenant, stored.FileName);
        await Assert.That(header!.KeyId).IsEqualTo("k1");
        await Assert.That(storage.CanUnprotect(header)).IsFalse();
        await Assert.That(_env.Storage.CanUnprotect(header)).IsTrue();

        var target = Path.Combine(_env.Directory, "restored");
        await Assert.That(async () => await storage.TryWritePlainToFileAsync(ArtifactCategories.Presweep, Tenant,
                stored.FileName, target))
            .Throws<UnknownSecretKeyIdException>();
        await Assert.That(File.Exists(target)).IsFalse();
    }

    [Test]
    public async Task EncryptedArtifactHeaders_ListTheKeyIdsOfAllCategories_ButNotPlainArtifacts()
    {
        await _env.Storage.StoreFileAsync(ArtifactCategories.Presweep, "t1", "a.presweep",
            _env.WriteFile("a", Payload(10)), ArtifactEncryption.Required);
        await _env.Storage.StoreFileAsync(ArtifactCategories.TenantDumps, "t2", "b.tar.gz",
            _env.WriteFile("b", Payload(10)), ArtifactEncryption.IfConfigured);
        await _env.Storage.StoreFileAsync(ArtifactCategories.RestoreStaging, "t2", "plain",
            _env.WriteFile("c", Payload(10)), ArtifactEncryption.None);

        var headers = await _env.Storage.GetEncryptedArtifactHeadersAsync();

        await Assert.That(headers.Select(h => $"{h.Category}/{h.TenantId}/{h.FileName}").OrderBy(x => x).ToArray())
            .IsEquivalentTo(new[] { "presweep/t1/a.presweep.octoenc", "tenant-dumps/t2/b.tar.gz.octoenc" });
        await Assert.That(headers.All(h => h.Header.KeyId == "k1")).IsTrue();
    }

    [Test]
    public async Task RestoreStaging_IsFoundEncryptedOrPlain_AndANestedOctoencUploadIsUnwrapped()
    {
        var dump = Payload(4000);
        // A client uploads an OCTOENC1 file of this environment; staging encrypts it once more.
        await using (var plainStream = new MemoryStream(dump))
        await using (var encrypted = File.Create(Path.Combine(_env.Directory, "upload.octoenc")))
        {
            await _env.Protector.ProtectAsync(plainStream, encrypted);
        }

        await _env.Storage.StoreFileAsync(ArtifactCategories.RestoreStaging, Tenant, "abc123",
            Path.Combine(_env.Directory, "upload.octoenc"), ArtifactEncryption.IfConfigured);
        await _env.Storage.StoreFileAsync(ArtifactCategories.RestoreStaging, Tenant, "plain1",
            _env.WriteFile("p", Payload(3)), ArtifactEncryption.None);

        var staged = await _env.Storage.FindRestoreStagingAsync(Tenant, "abc123");
        await Assert.That(staged!.Value.FileName).IsEqualTo("abc123.octoenc");
        await Assert.That((await _env.Storage.FindRestoreStagingAsync(Tenant, "plain1"))!.Value.FileName)
            .IsEqualTo("plain1");
        await Assert.That(await _env.Storage.FindRestoreStagingAsync(Tenant, "missing")).IsNull();
        await Assert.That(await _env.Storage.FindRestoreStagingAsync(Tenant, "../escape")).IsNull();
        await Assert.That(await _env.Storage.FindRestoreStagingAsync("other-tenant", "abc123")).IsNull();

        var target = Path.Combine(_env.Directory, "restore");
        await _env.Storage.TryWritePlainToFileAsync(ArtifactCategories.RestoreStaging, Tenant, "abc123.octoenc", target);
        await Assert.That((await File.ReadAllBytesAsync(target)).SequenceEqual(dump)).IsTrue();
    }

    [Test]
    public async Task DeleteOlderThan_DeletesOnlyExpiredArtifactsOfTheCategory()
    {
        await _env.Storage.StoreFileAsync(ArtifactCategories.TenantDumps, "t1", "old.tar.gz",
            _env.WriteFile("o", Payload(1)), ArtifactEncryption.None);
        await _env.Storage.StoreFileAsync(ArtifactCategories.TenantDumps, "t1", "new.tar.gz",
            _env.WriteFile("n", Payload(1)), ArtifactEncryption.None);
        await _env.Storage.StoreFileAsync(ArtifactCategories.Presweep, "t1", "p.presweep",
            _env.WriteFile("p", Payload(1)), ArtifactEncryption.Required);
        _env.Age(ArtifactCategories.TenantDumps, "t1", "old.tar.gz", TimeSpan.FromHours(30));
        _env.Age(ArtifactCategories.Presweep, "t1", "p.presweep.octoenc", TimeSpan.FromHours(30));

        var deleted = await _env.Storage.DeleteOlderThanAsync(ArtifactCategories.TenantDumps, TimeSpan.FromHours(24));

        await Assert.That(deleted.Select(d => d.FileName).ToArray()).IsEquivalentTo(new[] { "old.tar.gz" });
        await Assert.That(await _env.Storage.GetInfoAsync(ArtifactCategories.TenantDumps, "t1", "new.tar.gz"))
            .IsNotNull();
        await Assert.That(await _env.Storage.GetInfoAsync(ArtifactCategories.Presweep, "t1", "p.presweep.octoenc"))
            .IsNotNull();
    }

    [Test]
    public async Task ResultReference_RoundTrips_AndRejectsForeignValues()
    {
        var stored = new StoredArtifact(ArtifactCategories.TenantDumps, "t1", "x.tar.gz.octoenc", 1, true);

        var reference = stored.ToResultReference();

        await Assert.That(reference).IsEqualTo("octo-artifact:tenant-dumps/t1/x.tar.gz.octoenc");
        await Assert.That(StoredArtifact.TryParseResultReference(reference, out var parts)).IsTrue();
        await Assert.That(parts!.TenantId).IsEqualTo("t1");
        await Assert.That(StoredArtifact.TryParseResultReference("/tmp/octo-bot/dumps/t1/x.tar.gz", out _)).IsFalse();
        await Assert.That(StoredArtifact.TryParseResultReference("octo-artifact:tenant-dumps/../x", out _)).IsFalse();
        await Assert.That(StoredArtifact.TryParseResultReference("octo-artifact:a/b", out _)).IsFalse();
    }

    [Test]
    public async Task Scratch_IsOwnerOnly_AndStaleFilesAreCleanedUp()
    {
        var path = _env.Storage.CreateScratchFilePath(".presweep.tar.gz");
        await File.WriteAllTextAsync(path, "x");
        var fresh = _env.Storage.CreateScratchFilePath(".restore");
        await File.WriteAllTextAsync(fresh, "y");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-10));

        if (!OperatingSystem.IsWindows())
        {
            await Assert.That(File.GetUnixFileMode(_env.ScratchDirectory))
                .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await Assert.That(_env.Storage.CleanupScratch(TimeSpan.FromHours(4))).IsEqualTo(1);
        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(File.Exists(fresh)).IsTrue();
    }
}
