using System.Security.Cryptography;
using Meshmakers.Octo.Backend.BotServices.Controllers;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Backend.Jobs.Tests.Services;
using Meshmakers.Octo.Services.ArtifactStorage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Api;

/// <summary>
///     AB#6969 — a dump download must never look like a finished file when it is cut short: the response announces the
///     plain length up front (also for an encrypted artifact, which is decrypted while it is sent), and a failure after
///     the first bytes aborts the connection instead of ending the body cleanly.
/// </summary>
public class ArtifactDownloadResultTests : IDisposable
{
    private const string Tenant = "Tenant-A";
    private readonly ArtifactTestEnvironment _env = new("k1");

    public void Dispose()
    {
        _env.Dispose();
    }

    private async Task<string> StoreEncryptedAsync(int length)
    {
        var source = _env.WriteFile("dump", RandomNumberGenerator.GetBytes(length));
        var stored = await _env.Storage.StoreFileAsync(ArtifactCategories.TenantDumps, Tenant, "t.octobak.zip", source,
            ArtifactEncryption.IfConfigured);
        return stored.FileName;
    }

    private static (DefaultHttpContext Context, AbortProbe Probe, MemoryStream Body) NewContext()
    {
        var probe = new AbortProbe();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpRequestLifetimeFeature>(probe);
        var body = new MemoryStream();
        context.Response.Body = body;
        context.Features.Set<IHttpResponseFeature>(new BodyAwareResponseFeature(body));
        return (context, probe, body);
    }

    private ArtifactDownloadResult CreateResult(ArtifactDownload download)
    {
        return new ArtifactDownloadResult(download, _env.Storage, "application/zip", NullLogger.Instance);
    }

    [Test]
    public async Task EncryptedArtifact_AnnouncesThePlainLength_AndSendsExactlyThatManyBytes()
    {
        const int length = 3 * 1024 * 1024 + 7;
        var fileName = await StoreEncryptedAsync(length);
        await using var download = await _env.Storage.OpenDownloadAsync(ArtifactCategories.TenantDumps, Tenant, fileName);
        var (context, probe, body) = NewContext();

        await CreateResult(download!).ExecuteResultAsync(new ActionContext(context, new RouteData(), new()));

        await Assert.That(context.Response.ContentLength).IsEqualTo(length);
        await Assert.That(body.Length).IsEqualTo(length);
        await Assert.That(probe.Aborted).IsFalse();
    }

    [Test]
    public async Task TamperedArtifact_FailsAfterTheFirstBytes_AndAbortsTheConnectionWithTheLengthAnnounced()
    {
        var fileName = await StoreEncryptedAsync(3 * 1024 * 1024);
        var path = Path.Combine(_env.StoreRoot,
            _env.Keys.Build(ArtifactCategories.TenantDumps, Tenant, fileName).Replace('/', Path.DirectorySeparatorChar));
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[^100] ^= 0x5A;
        await File.WriteAllBytesAsync(path, bytes);
        await using var download = await _env.Storage.OpenDownloadAsync(ArtifactCategories.TenantDumps, Tenant, fileName);
        var (context, probe, body) = NewContext();

        await CreateResult(download!).ExecuteResultAsync(new ActionContext(context, new RouteData(), new()));

        // The announced length was set before any byte was sent and is still the announced one: the body is shorter, so
        // the client sees a failed transfer; the connection is aborted rather than finished.
        await Assert.That(context.Response.ContentLength).IsEqualTo(3 * 1024 * 1024);
        await Assert.That(body.Length).IsLessThan(3 * 1024 * 1024);
        await Assert.That(probe.Aborted).IsTrue();
    }

    private sealed class AbortProbe : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }

        public CancellationToken RequestAborted { get; set; }

        public void Abort()
        {
            Aborted = true;
        }
    }

    /// <summary>Behaves like a server response: the headers went out with the first byte of the body.</summary>
    private sealed class BodyAwareResponseFeature(MemoryStream body) : HttpResponseFeature
    {
        public override bool HasStarted => body.Length > 0;
    }
}
