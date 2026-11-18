using System.Security.Cryptography;

namespace Meshmakers.Octo.Backend.Jobs.DumpRegistry;

/// <summary>Streaming SHA-256 of a file or stream as lower-case hex.</summary>
public static class Sha256OfContent
{
    private const int BufferSize = 81920;

    /// <summary>The hash of <paramref name="content" />, read to its end.</summary>
    public static async Task<string> OfStreamAsync(Stream content, CancellationToken cancellationToken = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        int bytesRead;
        while ((bytesRead = await content.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, bytesRead);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>The hash of the file at <paramref name="path" />.</summary>
    public static async Task<string> OfFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await OfStreamAsync(file, cancellationToken);
    }

    /// <summary>The first characters of a hash, for log lines.</summary>
    public static string Prefix(string sha256) => sha256.Length <= 12 ? sha256 : sha256[..12];
}
