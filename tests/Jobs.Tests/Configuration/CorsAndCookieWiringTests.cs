using System.Runtime.CompilerServices;
using Meshmakers.Octo.Backend.BotServices;
using Meshmakers.Octo.Backend.BotServices.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Cors;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Configuration;

/// <summary>
///     AB#7270 (follow-up of AB#7265): bot-services was the only OctoMesh service outside the known-origins CORS
///     policy — <c>UseCors(p =&gt; p.AllowAnyOrigin()…)</c> answered every Origin with
///     <c>Access-Control-Allow-Origin: *</c> — its session cookie was <c>SameSite=None</c>, and the
///     <c>OctoIdentityAccessToken</c> cookie (a raw bearer token) was readable by scripts on every path.
///     <para>
///         The behaviour of the shared policy (unknown origin → no ACAO/ACAC on success and error responses,
///         known origin → ACAO, exposed headers) is tested in octo-common-services
///         (<c>KnownOriginsCorsPolicyTests</c>), the cookie attributes in its
///         <c>CookieBasedAuthenticationMiddlewareTests</c>. The composed pipeline lives in the top-level
///         statements of <c>Program.cs</c>, so its wiring is pinned at the source, like
///         <see cref="HangfireDashboardAuthorizationTests" /> does.
///     </para>
/// </summary>
internal class CorsAndCookieWiringTests
{
    [Test]
    public async Task TusHeaders_AreExposedOnTheSharedPolicy()
    {
        var options = new OctoCorsOptions();

        BotCorsConfiguration.Configure(options);

        await Assert.That(options.ExposedHeaders)
            .IsEquivalentTo(new[] { "Upload-Offset", "Upload-Length", "Tus-Resumable", "Location" });
        await Assert.That(options.PreflightMaxAge).IsEqualTo(TimeSpan.FromHours(1));
    }

    [Test]
    public async Task Configure_IsIdempotent()
    {
        var options = new OctoCorsOptions();

        BotCorsConfiguration.Configure(options);
        BotCorsConfiguration.Configure(options);

        await Assert.That(options.ExposedHeaders.Count).IsEqualTo(4);
    }

    [Test]
    public async Task Program_UsesTheSharedKnownOriginsPolicy_NotAWildcard()
    {
        var program = await ReadProgramAsync();

        await Assert.That(program).Contains("app.UseCors();");
        await Assert.That(program).DoesNotContain("AllowAnyOrigin");
        await Assert.That(program).DoesNotContain("AllowCredentials");
        await Assert.That(program).DoesNotContain("SetIsOriginAllowed");
        await Assert.That(program).Contains("builder.Services.Configure<OctoCorsOptions>(BotCorsConfiguration.Configure);");
    }

    [Test]
    public async Task Program_SessionCookieIsLax_NotNone()
    {
        var program = await ReadProgramAsync();

        await Assert.That(program).Contains("options.Cookie.SameSite = SameSiteMode.Lax;");
        await Assert.That(program).DoesNotContain("SameSiteMode.None");
        await Assert.That(program).Contains("options.Cookie.HttpOnly = true;");
        await Assert.That(program).Contains("options.Cookie.SecurePolicy = CookieSecurePolicy.Always;");
    }

    [Test]
    public async Task Program_LimitsTheAccessTokenCookieToTheDashboardBranch()
    {
        var program = await ReadProgramAsync();

        await Assert.That(BotServiceConstants.HangfireDashboardBranchPath).IsEqualTo("/ui");
        await Assert.That(program)
            .Contains("app.UseOctoCookieBasedAuthentication(BotServiceConstants.HangfireDashboardBranchPath);");
        await Assert.That(program).Contains("app.Map(BotServiceConstants.HangfireDashboardBranchPath,");
        await Assert.That(program).DoesNotContain("app.UseOctoCookieBasedAuthentication();");
    }

    private static Task<string> ReadProgramAsync() =>
        File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "src", "BotServices", "Program.cs"));

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
    }
}
