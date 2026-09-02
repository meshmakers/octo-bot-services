using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Meshmakers.Octo.Backend.BotServices;
using Meshmakers.Octo.Backend.BotServices.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Configuration;

/// <summary>
///     AB#5054 — pins the JWT bearer wiring the shared transport tenant gate
///     (<c>UseOctoTenantAuthorization()</c> / <c>TenantAuthorizationMiddleware</c>) depends on.
///     <para>
///         When AB#5054 landed, this service had <b>no</b> <c>{tenantId}</c> route segment — every
///         controller was routed <c>system/v{version}/[controller]</c> and a job's target tenant
///         travelled as a query argument or as TUS upload metadata — so the middleware returned
///         early on the missing route tenant and the gate changed nothing. The label was set (and
///         pinned here) anyway, so that the first tenant-scoped route added to this service would
///         arrive gated instead of silently unguarded, which is the exact failure AB#5054 exists to
///         remove.
///     </para>
///     <para>
///         AB#5060 added those routes (<c>TenantApi/v1/Controllers/JobsController</c>), so the gate
///         now fires here for real — and that is why the label matters rather than being
///         hypothetical. This service still keeps the platform default
///         <c>UserTokenEnforcement = Enforce</c> and does not opt down to the migration mode the way
///         asset-repo and the communication controller do. Those two stage because they are
///         narrowing a gate around callers that already exist; the tenant job routes are new and
///         have no callers to migrate, so enforcing them from their first release costs nothing and
///         is the whole point of having set the label early.
///     </para>
/// </summary>
internal class TenantAuthorizationWiringTests
{
    private static JwtBearerOptions Configure(string authority = "https://localhost:5003")
    {
        var options = new JwtBearerOptions();
        new ConfigureJwtBearerOptions(
                Options.Create(new OctoBotServicesOptions { AuthorityUrl = authority }))
            .Configure(options);
        return options;
    }

    /// <summary>
    ///     🔴 The silent-no-op trap. The middleware skips any principal whose
    ///     <c>AuthenticationType</c> is not <c>Bearer</c> — a guard against false 403s on the cookie
    ///     principal this service also issues. The JWT handler's default label is
    ///     <c>AuthenticationTypes.Federation</c>, so without this the gate never fires on a bearer
    ///     request.
    /// </summary>
    [Test]
    public async Task ConfigureJwtBearerOptions_LabelsTheIdentityBearer()
    {
        await Assert.That(Configure().TokenValidationParameters.AuthenticationType)
            .IsEqualTo(JwtBearerDefaults.AuthenticationScheme);
    }

    /// <summary>
    ///     The settings the configurator took over from the former <c>AddJwtBearer</c> delegate,
    ///     so consolidating the two did not change what a token has to satisfy.
    /// </summary>
    [Test]
    public async Task ConfigureJwtBearerOptions_KeepsAuthorityIssuerAndAudienceContract()
    {
        var options = Configure("https://identity.example.com");

        await Assert.That(options.Authority).IsEqualTo("https://identity.example.com/");
        // Trailing slash: IdentityServer stamps `iss` with one, so ValidIssuer must match exactly.
        await Assert.That(options.TokenValidationParameters.ValidIssuer)
            .IsEqualTo("https://identity.example.com/");
        await Assert.That(options.Audience).IsEqualTo("octoAPI");
    }

    /// <summary>
    ///     🔴 The test above proves nothing on its own — and that is not a figure of speech.
    ///     octo-ai-services had exactly that test, green, while the label was wiped at runtime: its
    ///     <c>Program.cs</c> configured the bearer scheme a <b>second</b> time via
    ///     <c>AddJwtBearer(jwt =&gt; { jwt.TokenValidationParameters = new TokenValidationParameters
    ///     { … }; })</c>. The options factory runs configurators in registration order, so the later
    ///     delegate replaced the whole instance — label and <c>ValidIssuer</c> gone — and the gate
    ///     was a no-op for a full release (AB#5051 → AB#5056). This service had the identical double
    ///     configuration until AB#5054.
    ///     <para>
    ///         The composed options cannot be resolved from a unit test (the registration lives in
    ///         top-level statements in <c>Program.cs</c>), so this guard pins the composition rule
    ///         at the source instead: exactly one configurator owns the scheme, and
    ///         <c>AddJwtBearer</c> is called without an argument. The OpenID Connect block in the
    ///         same file legitimately assigns its own <c>TokenValidationParameters</c> — different
    ///         options type, unaffected — which is why this checks the call and not the assignment.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ConfigureJwtBearerOptions_IsTheOnlyConfiguratorOfTheBearerScheme()
    {
        var program = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(),
            "src", "BotServices", "Program.cs"));

        await Assert.That(program).Contains("ConfigureOptions<ConfigureJwtBearerOptions>()");

        // Comments talk about the very pattern this guards against, so strip them first.
        var code = Regex.Replace(program, @"//.*?$", string.Empty, RegexOptions.Multiline);

        await Assert.That(Regex.IsMatch(code, @"AddJwtBearer\s*\(\s*[^)\s]")).IsFalse();
    }

    /// <summary>
    ///     Repository root, derived from this file's compile-time path so it is independent of the
    ///     build output directory.
    /// </summary>
    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
    }
}
