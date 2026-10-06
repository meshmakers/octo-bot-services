using System.Security.Claims;
using System.Text.Encodings.Web;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage.Monitoring;
using Meshmakers.Octo.Backend.BotServices;
using Meshmakers.Octo.Backend.BotServices.Configuration;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure;
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
using SystemSecretsController = Meshmakers.Octo.Backend.BotServices.SystemApi.v1.Controllers.SecretsController;
using TenantJobsController = Meshmakers.Octo.Backend.BotServices.TenantApi.v1.Controllers.JobsController;
using TenantSecretsController = Meshmakers.Octo.Backend.BotServices.TenantApi.v1.Controllers.SecretsController;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Api;

/// <summary>
///     The in-process host both job-API test classes run against: the two real
///     <c>JobsController</c>s behind the real <c>UseOctoTenantAuthorization()</c> middleware, with the
///     real <see cref="IJobTenantAccessGuard" />. Everything below the controllers is substituted —
///     no Mongo, no Hangfire server, no broker.
/// </summary>
/// <remarks>
///     <para>
///         Calling a controller method directly would prove nothing about the tenant routes: the whole
///         point of moving the tenant from <c>?tenantId=</c> into the route (AB#5060) is that a piece
///         of <b>middleware</b> reads the route value, so the gate can only be observed from outside
///         the endpoint. And for AB#5070 the two halves — the middleware's caller check and the
///         endpoint's ownership check — only compose in a real pipeline.
///     </para>
///     <para>
///         The tenant hierarchy has exactly one relation, <see cref="Parent" /> →
///         <see cref="Child" />; every other pair, including the reverse and any self-pair, answers
///         <c>false</c> (NSubstitute's default).
///     </para>
/// </remarks>
internal sealed class JobsApiTestHost : IDisposable
{
    public const string Parent = "parenttenant";
    public const string Child = "childtenant";
    public const string Unrelated = "othertenant";

    private const string SchemeName = "Bearer";

    private WebApplication _app = null!;
    private IBackgroundJobClient _backgroundJobClient = null!;
    private HttpClient _client = null!;
    private IJobStorageAccessor _jobStorage = null!;
    private ISecretSweepReportStore _secretSweepReportStore = null!;
    private ISecretSweepRunService _secretSweepRunService = null!;
    private ISecretEnvironmentStatusService _secretEnvironmentStatusService = null!;
    private string _tusFilePath = null!;

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

    public static async Task<JobsApiTestHost> StartAsync(Action<TenantAuthorizationOptions>? configure = null)
    {
        var fixture = new JobsApiTestHost();
        await fixture.InitializeAsync(configure);
        return fixture;
    }

    /// <summary>The user name carried by the user tokens (<c>name</c> claim).</summary>
    public const string UserName = "test-user";

    /// <summary>
    ///     A user token: carries <c>sub</c>, so the middleware takes the user path. Without
    ///     <paramref name="roles" /> it carries both secrets admin roles (AB#5544) - a tenant administrator -
    ///     so the gate tests are not about roles; pass roles explicitly (or none via
    ///     <see cref="UserTokenWithRoles" />) to test them.
    /// </summary>
    public static string UserToken(string tenantId)
    {
        return $"user:{tenantId}";
    }

    /// <summary>A user token with exactly <paramref name="roles" /> (possibly none).</summary>
    public static string UserTokenWithRoles(string tenantId, params string[] roles)
    {
        return $"user:{tenantId}|{string.Join(',', roles)}";
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
        return SendAsync(HttpMethod.Post, path, token);
    }

    public Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        return SendAsync(HttpMethod.Get, path, token);
    }

    public Task<HttpResponseMessage> DeleteAsync(string path, string token)
    {
        return SendAsync(HttpMethod.Delete, path, token);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
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

    /// <summary>
    ///     How many jobs were enqueued since the last <see cref="ResetJobClient" />. Asserting on zero
    ///     is how a test states that a request was refused <i>before</i> it did anything, rather than
    ///     merely that it answered with an error status.
    /// </summary>
    public int EnqueuedJobCount =>
        _backgroundJobClient.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IBackgroundJobClient.Create));

    public IBackgroundJobClient BackgroundJobClient => _backgroundJobClient;

    public IJobStorageAccessor JobStorage => _jobStorage;

    /// <summary>
    ///     The secret sweep report store (AB#5539), a substitute a test can seed.
    /// </summary>
    public ISecretSweepReportStore SecretSweepReportStore => _secretSweepReportStore;

    /// <summary>The secret sweep run service (AB#5544), a substitute a test can seed.</summary>
    public ISecretSweepRunService SecretSweepRunService => _secretSweepRunService;

    /// <summary>The secret environment status service (AB#5544), a substitute a test can seed.</summary>
    public ISecretEnvironmentStatusService SecretEnvironmentStatusService => _secretEnvironmentStatusService;

    /// <summary>
    ///     Makes <paramref name="jobId" /> resolvable, as a job that succeeded and left
    ///     <paramref name="resultPath" /> behind. <paramref name="job" /> is built with
    ///     <see cref="Job.FromExpression{T}(System.Linq.Expressions.Expression{Action{T}})" />, i.e.
    ///     exactly the shape Hangfire persists for a real enqueue — which is what makes the tenant
    ///     binding under test the real one rather than a hand-made stand-in.
    /// </summary>
    public void SeedSucceededJob(string jobId, Job job, string? resultPath)
    {
        var data = new Dictionary<string, string>();
        if (resultPath != null)
        {
            data["Result"] = resultPath;
        }

        _jobStorage.GetJobDetails(jobId).Returns(new JobDetailsDto
        {
            Job = job,
            CreatedAt = DateTime.UtcNow,
            History =
            [
                new StateHistoryDto
                {
                    StateName = "Succeeded",
                    CreatedAt = DateTime.UtcNow,
                    Data = data
                }
            ]
        });
    }

    /// <summary>
    ///     Makes <paramref name="jobId" /> resolvable as a job whose stored invocation could not be
    ///     deserialized — Hangfire's monitoring API answers such a job with a <c>null</c>
    ///     <see cref="JobDetailsDto.Job" />.
    /// </summary>
    public void SeedJobWithUnreadableInvocation(string jobId)
    {
        _jobStorage.GetJobDetails(jobId).Returns(new JobDetailsDto
        {
            Job = null,
            CreatedAt = DateTime.UtcNow,
            History =
            [
                new StateHistoryDto
                {
                    StateName = "Succeeded",
                    CreatedAt = DateTime.UtcNow,
                    Data = new Dictionary<string, string>()
                }
            ]
        });
    }

    private async Task InitializeAsync(Action<TenantAuthorizationOptions>? configure)
    {
        // A real, non-empty file: both upload-consuming operations verify the staged upload
        // before enqueuing, and a missing file would answer 404 for reasons unrelated to the gate.
        _tusFilePath = Path.Combine(Path.GetTempPath(), $"octo-bot-tus-{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(_tusFilePath, "backup");

        _backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        _backgroundJobClient.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("job-1");
        _backgroundJobClient.ChangeState(Arg.Any<string>(), Arg.Any<IState>(), Arg.Any<string>()).Returns(true);

        var backupFileStorage = Substitute.For<IBackupFileStorageService>();
        backupFileStorage.GetTusUploadFilePath(Arg.Any<string>(), Arg.Any<string>()).Returns(_tusFilePath);

        // AB#5070: the job store is a substitute so a test can seed a job of a chosen tenant; the
        // access guard below is the REAL one, because it is the thing under test.
        _jobStorage = Substitute.For<IJobStorageAccessor>();
        _secretSweepReportStore = Substitute.For<ISecretSweepReportStore>();
        _secretSweepRunService = Substitute.For<ISecretSweepRunService>();
        _secretEnvironmentStatusService = Substitute.For<ISecretEnvironmentStatusService>();

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
        builder.Services.AddSingleton(_jobStorage);
        builder.Services.AddSingleton(_secretSweepReportStore);
        builder.Services.AddSingleton(_secretSweepRunService);
        builder.Services.AddSingleton(_secretEnvironmentStatusService);
        // System tenant of the instance-wide secret sweep routes (AB#5539); default "OctoSystem".
        builder.Services.AddOptions<Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration.OctoSystemConfiguration>();
        builder.Services.AddScoped<IJobTenantAccessGuard, JobTenantAccessGuard>();

        if (configure != null)
        {
            builder.Services.AddOctoTenantAuthorization(configure);
        }

        // The same call Program.cs makes — without it the {tenantId:tenantId} templates 404. Calling
        // the shared extension rather than re-registering by hand means these tests run against the
        // real constraint, so a change to what may name a tenant shows up here.
        builder.Services.AddOctoTenantIdRouteConstraint();

        builder.Services.AddAuthentication(SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TokenShapedAuthenticationHandler>(SchemeName, _ => { });

        // The policies of Program.cs - the very same registration (AB#5544).
        builder.Services.AddAuthorization(options => options.AddBotPolicies());

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

    /// <summary>
    ///     Keeps the host to the controllers under test; the account and diagnostics
    ///     controllers of this assembly need services this fixture deliberately does not build.
    /// </summary>
    private sealed class JobsOnlyControllerFeatureProvider : ControllerFeatureProvider
    {
        protected override bool IsController(System.Reflection.TypeInfo typeInfo)
        {
            return base.IsController(typeInfo) &&
                   (typeInfo.AsType() == typeof(TenantJobsController) ||
                    typeInfo.AsType() == typeof(TenantSecretsController) ||
                    typeInfo.AsType() == typeof(SystemJobsController) ||
                    typeInfo.AsType() == typeof(SystemSecretsController));
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

            // "<kind>:<tenant>" = both secrets admin roles; "<kind>:<tenant>|<role>,<role>" = exactly these.
            var tenantAndRoles = parts[1].Split('|', 2);
            var roles = tenantAndRoles.Length == 2
                ? tenantAndRoles[1].Split(',', StringSplitOptions.RemoveEmptyEntries)
                : [CommonConstants.AdminPanelManagementRole, CommonConstants.SecretManagementRole];

            var isUser = parts[0] == "user";
            var claims = new List<Claim>
            {
                new("tenant_id", tenantAndRoles[0]),
                new("client_id", isUser ? "octo-cli" : "octo-worker"),
                new(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess)
            };
            claims.AddRange(roles.Select(r => new Claim("role", r)));

            if (isUser)
            {
                claims.Add(new Claim("sub", "test-subject"));
                claims.Add(new Claim("name", UserName));
            }

            // Name and role claim types as ConfigureJwtBearerOptions sets them.
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, "name", "role"));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
