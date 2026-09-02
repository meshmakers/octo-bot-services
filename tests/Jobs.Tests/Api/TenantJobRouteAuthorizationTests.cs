using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Meshmakers.Octo.Backend.BotServices;
using Meshmakers.Octo.Backend.BotServices.Routing;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.Core;
using SystemJobsController = Meshmakers.Octo.Backend.BotServices.SystemApi.v1.Controllers.JobsController;
using TenantJobsController = Meshmakers.Octo.Backend.BotServices.TenantApi.v1.Controllers.JobsController;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Api;

/// <summary>
///     AB#5060 — the tenant-routed job operations (<c>{tenantId}/v1/jobs/...</c>) and the transport
///     tenant gate that now reaches them.
/// </summary>
/// <remarks>
///     <para>
///         These run through a real request pipeline (<c>Microsoft.AspNetCore.TestHost</c>) hosting
///         the two real <c>JobsController</c>s behind the real <c>UseOctoTenantAuthorization()</c>
///         middleware. Calling a controller method directly would prove nothing here: the whole point
///         of moving the tenant from <c>?tenantId=</c> into the route is that a piece of
///         <b>middleware</b> reads the route value, so the gate can only be observed from outside the
///         endpoint. Nothing else of the host is booted — no Mongo, no Hangfire server, no broker; the
///         job client and the file storage are substitutes.
///     </para>
///     <para>
///         The scenarios per route are the contract of AB#5060: own tenant allowed (the equality case,
///         unchanged), a <b>parent user</b> token allowed on a child route (the new case, opened by
///         <c>[AllowParentTenantAdministration]</c>), an unrelated tenant refused, a <b>service</b>
///         token never admitted by the ancestor rule, and the same effect as the System-API variant it
///         replaces.
///     </para>
/// </remarks>
internal class TenantJobRouteAuthorizationTests
{
    private const string Parent = "parenttenant";
    private const string Child = "childtenant";
    private const string Unrelated = "othertenant";

    // The five tenant-addressed operations of this service, with the query arguments each needs
    // beyond the tenant. Both controllers serve exactly this set.
    private const string ExportQuery = "?archiveRtId=6512a1b2c3d4e5f601020304";
    private const string RestoreQuery = "?tusFileId=upload-1&databaseName=octo-child";
    private const string ImportQuery = "?tusFileId=upload-1&archiveRtId=6512a1b2c3d4e5f601020304";

    /// <summary>The equality case: a user token of the addressed tenant. Unchanged by AB#5060.</summary>
    [Test]
    [Arguments("run-fixup-scripts", "")]
    [Arguments("dump-repository", "")]
    [Arguments("export-archive-data", ExportQuery)]
    [Arguments("restore-from-upload", RestoreQuery)]
    [Arguments("import-archive-data-from-upload", ImportQuery)]
    public async Task TenantRoute_OwnTenantUserToken_IsAllowed(string route, string query)
    {
        using var host = await TestHostFixture.StartAsync();

        var response = await host.PostAsync($"/{Child}/v1/jobs/{route}{query}", TestHostFixture.UserToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    ///     🔴 The new case. A user token issued for the <b>parent</b> tenant reaches the child's
    ///     administration route, because the endpoint carries
    ///     <c>[AllowParentTenantAdministration]</c>. That is administration, not access: none of these
    ///     operations hands the caller the child's data, and no data route of this service is marked.
    /// </summary>
    [Test]
    [Arguments("run-fixup-scripts", "")]
    [Arguments("dump-repository", "")]
    [Arguments("export-archive-data", ExportQuery)]
    [Arguments("restore-from-upload", RestoreQuery)]
    [Arguments("import-archive-data-from-upload", ImportQuery)]
    public async Task TenantRoute_ParentUserToken_IsAllowedOnChildRoute(string route, string query)
    {
        using var host = await TestHostFixture.StartAsync();

        var response = await host.PostAsync($"/{Child}/v1/jobs/{route}{query}", TestHostFixture.UserToken(Parent));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    ///     A user token of a tenant that is neither the addressed one nor its parent is refused. The
    ///     marker widens the gate by exactly one relation, never into a blanket relaxation.
    /// </summary>
    [Test]
    [Arguments("run-fixup-scripts", "")]
    [Arguments("dump-repository", "")]
    [Arguments("export-archive-data", ExportQuery)]
    [Arguments("restore-from-upload", RestoreQuery)]
    [Arguments("import-archive-data-from-upload", ImportQuery)]
    public async Task TenantRoute_UnrelatedUserToken_IsForbidden(string route, string query)
    {
        using var host = await TestHostFixture.StartAsync();

        var response = await host.PostAsync($"/{Child}/v1/jobs/{route}{query}", TestHostFixture.UserToken(Unrelated));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    /// <summary>
    ///     🔴 A <b>service</b> token of the parent is refused on the child's route even though the
    ///     endpoint is marked: the ancestor rule is user-token only, because a client-credentials
    ///     token's <c>tenant_id</c> proves nothing (mirrored clients share the parent's secret). Run
    ///     with <c>ServiceTokenEnforcement = Enforce</c>, because the platform default
    ///     (<c>LogOnly</c>) changes no outcome and would hide the refusal behind the AB#5032 staging.
    /// </summary>
    [Test]
    [Arguments("run-fixup-scripts", "")]
    [Arguments("dump-repository", "")]
    [Arguments("export-archive-data", ExportQuery)]
    [Arguments("restore-from-upload", RestoreQuery)]
    [Arguments("import-archive-data-from-upload", ImportQuery)]
    public async Task TenantRoute_ParentServiceToken_IsNotAllowedByTheAncestorRule(string route, string query)
    {
        using var host = await TestHostFixture.StartAsync(
            o => o.ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Enforce);

        var response = await host.PostAsync($"/{Child}/v1/jobs/{route}{query}", TestHostFixture.ServiceToken(Parent));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    /// <summary>
    ///     A service token issued for the addressed tenant itself keeps working — the rule above
    ///     removes the ancestor shortcut, not the exact match every deployed worker relies on.
    /// </summary>
    [Test]
    [Arguments("run-fixup-scripts", "")]
    [Arguments("dump-repository", "")]
    [Arguments("export-archive-data", ExportQuery)]
    [Arguments("restore-from-upload", RestoreQuery)]
    [Arguments("import-archive-data-from-upload", ImportQuery)]
    public async Task TenantRoute_OwnServiceToken_IsAllowed(string route, string query)
    {
        using var host = await TestHostFixture.StartAsync(
            o => o.ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Enforce);

        var response = await host.PostAsync($"/{Child}/v1/jobs/{route}{query}", TestHostFixture.ServiceToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    ///     Same operation, same effect: the tenant route enqueues the identical Hangfire job — same
    ///     job type, same method, same arguments — as the deprecated System-API route it replaces.
    ///     That is what makes the System variant safe to keep as a fallback until stage 3 removes it,
    ///     and it is checked rather than argued because the two surfaces are two controllers.
    /// </summary>
    [Test]
    [Arguments("run-fixup-scripts", "")]
    [Arguments("dump-repository", "")]
    [Arguments("export-archive-data", ExportQuery)]
    [Arguments("restore-from-upload", RestoreQuery)]
    [Arguments("import-archive-data-from-upload", ImportQuery)]
    public async Task TenantRoute_EnqueuesTheSameJobAsTheSystemRoute(string route, string query)
    {
        using var host = await TestHostFixture.StartAsync();

        var tenantResponse = await host.PostAsync($"/{Child}/v1/jobs/{route}{query}",
            TestHostFixture.UserToken(Child));
        await Assert.That(tenantResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var viaTenantRoute = host.LastEnqueuedJob();

        host.ResetJobClient();

        var separator = query.Length == 0 ? "?" : "&";
        var systemResponse = await host.PostAsync($"/system/v1/jobs/{route}{query}{separator}tenantId={Child}",
            TestHostFixture.UserToken(Child));
        await Assert.That(systemResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var viaSystemRoute = host.LastEnqueuedJob();

        await Assert.That(viaTenantRoute.Type).IsEqualTo(viaSystemRoute.Type);
        await Assert.That(viaTenantRoute.Method.Name).IsEqualTo(viaSystemRoute.Method.Name);
        var sameArguments = viaTenantRoute.Args.Select(a => a?.ToString())
            .SequenceEqual(viaSystemRoute.Args.Select(a => a?.ToString()));
        await Assert.That(sameArguments).IsTrue();
    }

    /// <summary>
    ///     The System-API variants keep working untouched — including for a caller whose token was
    ///     issued for a different tenant, because the gate reads the route value and that route has
    ///     none. That is precisely the hole the tenant routes close; pinning it here records that
    ///     removing the System variants (stage 3) is the fix, not a regression.
    /// </summary>
    [Test]
    [Arguments("run-fixup-scripts", "")]
    [Arguments("dump-repository", "")]
    [Arguments("export-archive-data", ExportQuery)]
    [Arguments("restore-from-upload", RestoreQuery)]
    [Arguments("import-archive-data-from-upload", ImportQuery)]
    public async Task SystemRoute_StaysFunctionalAndUngated(string route, string query)
    {
        using var host = await TestHostFixture.StartAsync();

        var separator = query.Length == 0 ? "?" : "&";
        var response = await host.PostAsync($"/system/v1/jobs/{route}{query}{separator}tenantId={Child}",
            TestHostFixture.UserToken(Unrelated));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    ///     🔴 The marker is what opens the parent path, so where it sits is part of the contract: on
    ///     the tenant job routes and on nothing else in this service. In particular it must never
    ///     reach a route that returns tenant content.
    /// </summary>
    [Test]
    public async Task OnlyTheTenantJobRoutesCarryTheParentAdministrationMarker()
    {
        var marked = typeof(TenantJobsController).Assembly.GetTypes()
            .Where(IsMarked)
            .ToArray();

        await Assert.That(marked.Length).IsEqualTo(1);
        await Assert.That(marked[0]).IsEqualTo(typeof(TenantJobsController));

        static bool IsMarked(Type type)
        {
            return type.GetCustomAttributes(typeof(IAllowParentTenantAdministration), true).Length != 0 ||
                   type.GetMethods().Any(m =>
                       m.GetCustomAttributes(typeof(IAllowParentTenantAdministration), true).Length != 0);
        }
    }

    /// <summary>
    ///     The in-process host: the two real controllers, the real tenant gate, everything else faked.
    /// </summary>
    private sealed class TestHostFixture : IDisposable
    {
        private const string SchemeName = "Bearer";

        private WebApplication _app = null!;
        private HttpClient _client = null!;
        private IBackgroundJobClient _backgroundJobClient = null!;
        private string _tusFilePath = null!;

        public static async Task<TestHostFixture> StartAsync(Action<TenantAuthorizationOptions>? configure = null)
        {
            var fixture = new TestHostFixture();
            await fixture.InitializeAsync(configure);
            return fixture;
        }

        /// <summary>A user token: carries <c>sub</c>, so the middleware takes the user path.</summary>
        public static string UserToken(string tenantId)
        {
            return $"user:{tenantId}";
        }

        /// <summary>
        ///     A client-credentials token: no <c>sub</c>, which is exactly how the middleware tells a
        ///     service token apart from a user token.
        /// </summary>
        public static string ServiceToken(string tenantId)
        {
            return $"service:{tenantId}";
        }

        public Task<HttpResponseMessage> PostAsync(string path, string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            return _client.SendAsync(request);
        }

        /// <summary>The Hangfire job the last request enqueued.</summary>
        public Job LastEnqueuedJob()
        {
            var call = _backgroundJobClient.ReceivedCalls()
                .Last(c => c.GetMethodInfo().Name == nameof(IBackgroundJobClient.Create));
            return (Job)call.GetArguments()[0]!;
        }

        public void ResetJobClient()
        {
            _backgroundJobClient.ClearReceivedCalls();
        }

        private async Task InitializeAsync(Action<TenantAuthorizationOptions>? configure)
        {
            // A real, non-empty file: both upload-consuming operations verify the staged upload
            // before enqueuing, and a missing file would answer 404 for reasons unrelated to the gate.
            _tusFilePath = Path.Combine(Path.GetTempPath(), $"octo-bot-tus-{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(_tusFilePath, "backup");

            _backgroundJobClient = Substitute.For<IBackgroundJobClient>();
            _backgroundJobClient.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("job-1");

            var backupFileStorage = Substitute.For<IBackupFileStorageService>();
            backupFileStorage.GetTusUploadFilePath(Arg.Any<string>()).Returns(_tusFilePath);

            // parenttenant -> childtenant is the only relation in this hierarchy; every other pair,
            // including the reverse and any self-pair, answers false (NSubstitute's default).
            var hierarchy = Substitute.For<ITenantHierarchyReader>();
            hierarchy.IsChildTenantAsync(Parent, Child).Returns(true);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            builder.Services.AddSingleton(_backgroundJobClient);
            builder.Services.AddSingleton(backupFileStorage);
            builder.Services.AddSingleton(Substitute.For<IDistributedCacheService>());
            builder.Services.AddSingleton(hierarchy);

            if (configure != null)
            {
                builder.Services.AddOctoTenantAuthorization(configure);
            }

            // Same registration as Program.cs — without it the {tenantId:tenantId} templates 404.
            builder.Services.Configure<RouteOptions>(options =>
                options.ConstraintMap.Add("tenantId", typeof(TenantIdRouteConstraint)));

            builder.Services.AddAuthentication(SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TokenShapedAuthenticationHandler>(SchemeName, _ => { });

            // The two policies of Program.cs, verbatim.
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy(BotServiceConstants.JobApiReadOnlyPolicy, policy =>
                    policy.RequireClaim(InfrastructureCommon.ClaimScope,
                        CommonConstants.OctoApiFullAccess, CommonConstants.OctoApiReadOnly));
                options.AddPolicy(BotServiceConstants.JobApiReadWritePolicy, policy =>
                    policy.RequireClaim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess));
            });

            builder.Services.AddApiVersioning().AddMvc();
            builder.Services.AddControllers()
                .ConfigureApplicationPartManager(manager =>
                {
                    manager.ApplicationParts.Clear();
                    manager.ApplicationParts.Add(new AssemblyPart(typeof(TenantJobsController).Assembly));
                    foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                    {
                        manager.FeatureProviders.Remove(provider);
                    }

                    manager.FeatureProviders.Add(new JobsOnlyControllerFeatureProvider());
                });

            _app = builder.Build();
            _app.UseRouting();
            _app.UseAuthentication();
            _app.UseAuthorization();
            _app.UseOctoTenantAuthorization();
            _app.MapControllers();

            await _app.StartAsync();
            _client = _app.GetTestClient();
        }

        public void Dispose()
        {
            _client.Dispose();
            _app.StopAsync().GetAwaiter().GetResult();
            ((IDisposable)_app).Dispose();
            if (File.Exists(_tusFilePath))
            {
                File.Delete(_tusFilePath);
            }
        }

        /// <summary>
        ///     Keeps the host to the two controllers under test; the account and diagnostics
        ///     controllers of this assembly need services this fixture deliberately does not build.
        /// </summary>
        private sealed class JobsOnlyControllerFeatureProvider : ControllerFeatureProvider
        {
            protected override bool IsController(System.Reflection.TypeInfo typeInfo)
            {
                return base.IsController(typeInfo) &&
                       (typeInfo.AsType() == typeof(TenantJobsController) ||
                        typeInfo.AsType() == typeof(SystemJobsController));
            }
        }

        /// <summary>
        ///     Turns the bearer value into the principal shape the gate keys off: the identity is
        ///     labelled <c>Bearer</c> (the label the JWT handler only carries because
        ///     <c>ConfigureJwtBearerOptions</c> sets it — AB#5054), and a service token is modelled the
        ///     way the middleware detects one, by the <b>absence</b> of a <c>sub</c> claim.
        /// </summary>
        private sealed class TokenShapedAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
        {
            protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            {
                var header = Request.Headers.Authorization.ToString();
                if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(AuthenticateResult.NoResult());
                }

                var parts = header["Bearer ".Length..].Split(':', 2);
                if (parts.Length != 2)
                {
                    return Task.FromResult(AuthenticateResult.Fail("Malformed test token"));
                }

                var isUser = parts[0] == "user";
                var claims = new List<Claim>
                {
                    new("tenant_id", parts[1]),
                    new("client_id", isUser ? "octo-cli" : "octo-worker"),
                    new(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess)
                };

                if (isUser)
                {
                    claims.Add(new Claim("sub", "test-subject"));
                }

                var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
                return Task.FromResult(AuthenticateResult.Success(
                    new AuthenticationTicket(principal, SchemeName)));
            }
        }
    }
}
