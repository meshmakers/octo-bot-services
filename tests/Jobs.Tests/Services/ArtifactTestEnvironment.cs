using System.Security.Cryptography;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Runtime.Engine.Secrets;
using Meshmakers.Octo.Services.ArtifactStorage;
using Meshmakers.Octo.Services.ArtifactStorage.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Services;

/// <summary>
///     A real file system artifact store in a temp directory plus the engine's real <see cref="ISecretFileProtector" />
///     over generated keys (AB#5559, AB#5561). Key material is random per test run and never printed.
/// </summary>
internal sealed class ArtifactTestEnvironment : IDisposable
{
    public const string InstancePrefix = "test";

    public ArtifactTestEnvironment(params string[] keyIds)
    {
        Directory = Path.Combine(Path.GetTempPath(), $"octo-artifacts-test-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Directory);
        foreach (var keyId in keyIds)
        {
            Ring[keyId] = RandomNumberGenerator.GetBytes(32);
        }

        Store = new FileSystemArtifactStore(StoreRoot);
        Protector = CreateProtector(Ring, keyIds.FirstOrDefault());
        Storage = CreateStorage(Protector);
    }

    /// <summary>Root temp directory (store, scratch, test files).</summary>
    public string Directory { get; }

    /// <summary>The generated key ring (key id to 32-byte key).</summary>
    public Dictionary<string, byte[]> Ring { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FileSystemArtifactStore Store { get; }

    public ArtifactKeyBuilder Keys { get; } = new(InstancePrefix);

    public ISecretFileProtector Protector { get; }

    public BotArtifactStorage Storage { get; }

    public string ScratchDirectory => Path.Combine(Directory, "scratch");

    /// <summary>
    ///     The same store seen through another key ring (rotation: a key id removed, or no keys at all).
    /// </summary>
    public BotArtifactStorage CreateStorage(ISecretFileProtector protector)
    {
        return new BotArtifactStorage(Store, Keys, protector, ScratchDirectory,
            NullLogger<BotArtifactStorage>.Instance);
    }

    /// <summary>
    ///     The engine's file protector over <paramref name="ring" /> with <paramref name="activeKeyId" />.
    /// </summary>
    public static ISecretFileProtector CreateProtector(IReadOnlyDictionary<string, byte[]> ring, string? activeKeyId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRuntimeEngine();
        services.Configure<SecretEncryptionOptions>(options =>
        {
            foreach (var (keyId, key) in ring)
            {
                options.Keys[keyId] = Convert.ToBase64String(key);
            }

            options.ActiveKeyId = activeKeyId;
        });
        return services.BuildServiceProvider().GetRequiredService<ISecretFileProtector>();
    }

    /// <summary>Root of the file system store.</summary>
    public string StoreRoot => Path.Combine(Directory, "store");

    /// <summary>Backdates an artifact (the file system store reports the last write time as its creation time).</summary>
    public void Age(string category, string tenantId, string fileName, TimeSpan age)
    {
        var key = Keys.Build(category, tenantId, fileName);
        File.SetLastWriteTimeUtc(Path.Combine(StoreRoot, key.Replace('/', Path.DirectorySeparatorChar)),
            DateTime.UtcNow - age);
    }

    /// <summary>Writes a local file with <paramref name="content" /> and returns its path.</summary>
    public string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(Directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>The raw stored bytes of an artifact.</summary>
    public async Task<byte[]> ReadStoredAsync(string category, string tenantId, string fileName)
    {
        await using var stream = await Store.OpenReadAsync(Keys.Build(category, tenantId, fileName));
        using var buffer = new MemoryStream();
        await stream!.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, true);
        }
        catch (IOException)
        {
        }
    }
}
