using System.IdentityModel.Tokens.Jwt;
using System.Text;
using BotServices.Resources;
using Duende.IdentityModel;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using Meshmakers.Octo.Backend.BotServices;
using Meshmakers.Octo.Backend.BotServices.Configuration;
using Meshmakers.Octo.Backend.BotServices.Consumers;
using Meshmakers.Octo.Backend.BotServices.HangfireSetup;
using Meshmakers.Octo.Backend.BotServices.Methods;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Extensions;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Cors;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.ArtifactStorage;
using Meshmakers.Octo.Services.Observability;
using Meshmakers.Octo.Services.Swagger.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using NLog;
using NLog.Web;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

// NLog: set up the logger first to catch all errors
var nLogFactory = LogManager.Setup().RegisterNLogWeb().LoadConfigurationFromFile("nlog.config").LogFactory;
var logger = nLogFactory.GetCurrentClassLogger();


try
{
    logger.Debug("init main");

    var builder = WebApplication.CreateBuilder(args);
    builder.AddObservability()
        .AddSystemContextHealthCheck()
        // AB#5561: reachability of the artifact store (pre-sweep dumps, tenant dumps, restore staging). Degraded,
        // not "ready": an unreachable store breaks dumps, restores and writing sweeps, not the other jobs.
        .AddArtifactStorageHealthCheck(failureStatus: HealthStatus.Degraded, tags: ["artifact-storage"]);

    JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

    builder.Services.Configure<OctoBotServicesOptions>(options =>
        builder.Configuration.GetSection("Bot").Bind(options));
    builder.Services.Configure<OctoSystemConfiguration>(options =>
        builder.Configuration.GetSection("System").Bind(options));
    // AB#5539: secret sweep (Bot:SecretSweep, env OCTO_BOT__SECRETSWEEP__*). The key ring is the engine's
    // SecretEncryption section, bound by AddRuntimeEngine().
    builder.Services.Configure<SecretSweepJobOptions>(options =>
        builder.Configuration.GetSection(SecretSweepJobOptions.SectionName).Bind(options));

    // additional providers here needed.
    // allow environment variables to override values from other providers.
    builder.Configuration.AddEnvironmentVariables("OCTO_").AddCommandLine(args)
        .AddUserSecrets(typeof(Program).Assembly, true);


    builder.Services.ConfigureOptions<ConfigureJwtBearerOptions>();
    builder.Services.ConfigureOptions<ConfigureOpenIdConnectOptions>();
    builder.Services.ConfigureOptions<ConfigureOctoOpenApiOptions>();
    builder.Services.ConfigureOptions<ConfigureDistributionEventHubOptions>();


    builder.Services.AddTransient<IJobCreatorService, JobCreatorService>();

    // AB#5070: a job artifact belongs to the tenant the job ran for, and the job-instance endpoints
    // (status, download, delete) enforce that themselves — the System routes they live on carry no
    // tenant segment, so UseOctoTenantAuthorization() returns early there and checks nothing at all.
    // JobTenantAccessGuard is the middleware's decision performed in code; HangfireJobStorageAccessor
    // is the seam over JobStorage.Current that makes it testable.
    builder.Services.AddSingleton<IJobStorageAccessor, HangfireJobStorageAccessor>();
    builder.Services.AddScoped<IJobTenantAccessGuard, JobTenantAccessGuard>();

    builder.Services.AddCors();
    // AB#7270: the CORS policy is the shared known-origins policy registered by AddOctoServiceInfrastructure()
    // (allowed origins = registered client origins of the addressed tenant, never credentials); bots only adds
    // the tus response headers the browser upload client reads.
    builder.Services.Configure<OctoCorsOptions>(BotCorsConfiguration.Configure);

    // AB#5060: this service now serves a tenant-addressed surface as well
    // ({tenantId}/v1/jobs/dump-repository, restore-from-upload, export-archive-data,
    // import-archive-data-from-upload, run-fixup-scripts), so the `tenantId` route constraint every
    // other tenant-serving OctoMesh host registers is needed here too. Without it the
    // `{tenantId:tenantId}` templates of TenantApi never match and the routes 404.
    builder.Services.AddOctoTenantIdRouteConstraint();

    // AB#5032 (wired here with AB#5047): lets an operator narrow the client-credentials
    // exemption of UseOctoTenantAuthorization() per environment (OCTO_TENANTAUTHORIZATION__…).
    // The defaults reproduce the previous behaviour and only add the audit log.
    //
    // AB#5054 set TokenValidationParameters.AuthenticationType = "Bearer"
    // (Configuration/ConfigureJwtBearerOptions.cs) so the middleware stops being a silent no-op on
    // bearer requests here. Unlike asset-repo and the communication controller this service does
    // NOT opt its user path down to UserTokenEnforcement=LogOnly: at the time of AB#5054 there was
    // no {tenantId} route segment in this service at all, so the platform default (Enforce) meant a
    // future tenant-scoped route would arrive closed rather than open.
    //
    // AB#5060 is that future: TenantApi/v1/Controllers/JobsController serves the five
    // tenant-addressed job operations on {tenantId}/v1/jobs/... and the gate now really runs on
    // them. It stays on Enforce — the routes are new, so there is no installed caller base to stage
    // for; the deprecated system/v1/jobs/...?tenantId=… variants keep working meanwhile because the
    // middleware reads the ROUTE value and those carry none. The five tenant routes additionally
    // carry [AllowParentTenantAdministration], which lets an administrator of the parent tenant back
    // up / restore / export a child (user tokens only, never service tokens).
    builder.Services.AddOctoTenantAuthorization(builder.Configuration);

    builder.Services.AddScoped<IDefaultConfigurationCreatorService, DefaultConfigurationCreatorService>();

    builder.Services.AddMemoryCache();

    builder.Services.AddOctoServiceInfrastructure("BotService",
        c =>
        {
            c.AddHangfireMessageScheduler();

            // AB#6910: the six command consumers are shared (competing consumers over all replicas, each command
            // processed once); see BotCommandConsumers for why each one is safe and why MigratesLegacyCommand is set.
            c.AddBotCommandConsumers();
            c.AddCommandClient<CreateIdentityDataCommandRequest>(QueueNames.CreateIdentityDataCommand);
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PosCreateTenant>();
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PosUpdateTenant>();
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PreDeleteTenant>();
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PosDeleteTenant>();

            // AB#8445: answers the method dispatchers' discovery with an immediate announcement of the handlers below.
            c.AddCkMethodHandlerDiscovery();
        });

    // AB#8445 (F3.6): CK method host - System.Bot/TenantRepository.Dump-1 (async, operation handle).
    builder.Services.AddBotCkMethodHost();

    builder.Services.AddRuntimeEngine()
        .AddMongoDbRuntimeRepository()
        // AB#4230: the archive data export/import jobs access the tenant's CrateDB stream-data
        // store directly (like Dump/RestoreRepositoryJob access MongoDB directly) instead of calling
        // the asset-repo over HTTP. BotConfigureStreamDataConfiguration supplies the CrateDB
        // connection from Bot:StreamData* options.
        .AddCrateDbStreamDataRepository<BotConfigureStreamDataConfiguration>();

    // Register the StreamData CK model descriptor so the archive CK types (CkArchive /
    // CkRollupArchive) resolve when the jobs read an ArchiveSnapshot from the runtime store.
    // Same registration as asset-repo Program.cs.
    builder.Services.AddSingleton<Meshmakers.Octo.Runtime.Contracts.MongoDb.Services.IStreamDataCkModelDescriptor>(
        _ => new Meshmakers.Octo.Runtime.Contracts.MongoDb.Services.StreamDataCkModelDescriptor(
            Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1.SystemStreamDataCkIds.CkModelId));

    // OctoBotServicesOptions are bound later; read them directly for AddOctoJobs parameters
    var botOptions = new OctoBotServicesOptions();
    builder.Configuration.GetSection("Bot").Bind(botOptions);

    var secretSweepOptions = new SecretSweepJobOptions();
    builder.Configuration.GetSection(SecretSweepJobOptions.SectionName).Bind(secretSweepOptions);

    builder.Services.AddOctoJobs(
        tusStoragePath: botOptions.TusStoragePath,
        dumpStoragePath: botOptions.DumpStoragePath,
        fileRetentionHours: botOptions.FileRetentionHours,
        secretBackupStoragePath: secretSweepOptions.BackupStoragePath,
        secretBackupRetentionDays: secretSweepOptions.BackupRetentionDays,
        artifactRetentionHours: botOptions.ArtifactRetentionHours);
    // AB#5561: platform artifact store (ArtifactStorage section, OCTO_ARTIFACTSTORAGE__*). Without configuration it
    // is the file system below <temp>/octo-bot/artifacts, so a local start needs no new settings. Precedence of the
    // file system root and the Bot:SecretSweep:BackupStoragePath alias: see AddOctoBotArtifactStorage / README.
    builder.Services.AddOctoBotArtifactStorage(builder.Configuration, botOptions.ScratchPath);
    builder.Services.AddHostedService<ArtifactStoreStartupLogService>();
    builder.Services.AddOctoNotification();
    builder.Services.AddCkModelSystemBotV3();

    builder.Services.AddAuthentication(authenticationOptions =>
        {
            authenticationOptions.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            authenticationOptions.DefaultChallengeScheme = InfrastructureCommon.OidcAuthenticationScheme;
        })
        .AddCookie(options =>
        {
            options.ExpireTimeSpan = BotServiceConstants.CookieExpireTimeSpan;

            // add an instance of the patched manager to the options:
            options.CookieManager = new ChunkingCookieManager();
            options.Cookie.Name = BotServiceConstants.CookieName;
            options.Cookie.HttpOnly = true;
            // AB#7270: Lax, not None. The session cookie of the interactive OIDC login is only needed on
            // top-level navigations to this host (the Hangfire dashboard, the OIDC callback). Identity and bots
            // share the cluster's site, so the form_post callback from the identity server is a same-site
            // request; the SPAs call the API with bearer headers and never rely on this cookie. No cross-site
            // flow needs SameSite=None, which would send the cookie on any third-party request.
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;

            // AB#5059: answer a forbidden request with 403 instead of the cookie handler's default
            // 302 to /Account/AccessDenied, a path this service does not serve — which would turn
            // every scope-based refusal on the Hangfire dashboard branch into a bare 404 and read as
            // "broken" rather than "denied". This service is an API host plus the dashboard; it has
            // no access-denied page, and until AB#5059 nothing on the cookie scheme could forbid at
            // all (the /ui branch only ever required an authenticated user), so no existing flow
            // changes.
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        })
        .AddOpenIdConnect(InfrastructureCommon.OidcAuthenticationScheme, options =>
        {
            options.ClientId = CommonConstants.BotServicesClientId;

            // AB#5266 — must be stated explicitly. The handler default is the implicit
            // id_token flow, which the identity server stopped supporting when it moved from
            // Duende to OpenIddict (AB#4989): OpenIddict enables the authorization code flow
            // only, so the challenge dies at the PAR endpoint with 'unsupported_response_type'
            // before any application code runs — a bodyless 500 on every interactive login.
            options.ResponseType = OpenIdConnectResponseType.Code;

            options.Scope.Clear();
            options.Scope.Add(CommonConstants.Scopes.OpenId);
            options.Scope.Add(CommonConstants.Scopes.Profile);
            options.Scope.Add(CommonConstants.Scopes.Email);
            options.Scope.Add(CommonConstants.Scopes.Role);

            options.SaveTokens = true;
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.GetClaimsFromUserInfoEndpoint = true;

            options.TokenValidationParameters = new TokenValidationParameters
            {
                NameClaimType = JwtClaimTypes.Name,
                RoleClaimType = JwtClaimTypes.Role
            };
            // 🔴 AB#5054 — no configuration delegate here. Audience, claim types, issuer and the
            // "Bearer" AuthenticationType all live in ConfigureJwtBearerOptions (registered at the
            // top of this file). A delegate here runs LAST in the options factory, so an assignment
            // to TokenValidationParameters silently discards what the configurator set — including
            // the label TenantAuthorizationMiddleware keys its tenant check off, which turns the
            // gate back into a no-op with no compile error and no red test. See the remarks on
            // ConfigureJwtBearerOptions. (The OIDC block above is a different options type.)
        }).AddJwtBearer();


    // AB#5544: the policies live in BotAuthorizationPolicies so the API tests use the very same ones.
    builder.Services.AddAuthorization(options => options.AddBotPolicies());

    builder.Services.AddMvcCore().AddAuthorization();
    builder.Services.AddMvc();

    builder.Services.AddOctoApiVersioningAndDocumentation(options =>
    {
        options.Scopes = new Dictionary<string, string>
        {
            {
                CommonConstants.OctoApiFullAccess,
                CommonConstants.OctoApiFullAccessDisplayName
            },
            {
                CommonConstants.OctoApiReadOnly,
                CommonConstants.OctoApiReadOnlyDisplayName
            }
        };

        options.PolicyScopeMapping = new Dictionary<string, IEnumerable<string>>
        {
            { BotServiceConstants.JobApiReadOnlyPolicy, [CommonConstants.OctoApiReadOnly] },
            { BotServiceConstants.JobApiReadWritePolicy, [CommonConstants.OctoApiFullAccess] },
            { BotServiceConstants.TenantJobManagementPolicy, [CommonConstants.OctoApiFullAccess] },
            { BotServiceConstants.SecretManagementPolicy, [CommonConstants.OctoApiFullAccess] },
            { BotServiceConstants.SecretAdministrationReadPolicy, [CommonConstants.OctoApiReadOnly] }
        };
        
        options.XmlDocDataTransferObjectAssemblies = [typeof(JobDto).Assembly];
        options.XmlDocOperationAssemblies = [typeof(Program).Assembly];

        options.ApiTitle = BotTexts.ApiName;
        options.ApiDescription = BotTexts.ApiDescription;

        options.ClientId = CommonConstants.BotServicesSwaggerClientId;
        options.AppName = BotTexts.Backend_BotServices_UserSchema_Swagger_DisplayName;
    }).AddVersion();

    // Hangfire is used to handle background jobs and scheduled jobs
    builder.Services.AddHangfire((serviceProvider, config) =>
    {
        var octoBotServicesOptions = serviceProvider.GetRequiredService<IOptions<OctoBotServicesOptions>>();
        var systemOptions = serviceProvider.GetRequiredService<IOptions<OctoSystemConfiguration>>();

        // AB#7196: migrate in place with a collection backup; never drop recurring jobs.
        var storageOptions = HangfireMongoStorageSetup.CreateStorageOptions();
        var mongoUrlBuilder = new MongoUrlBuilder
        {
            DatabaseName = string.IsNullOrWhiteSpace(octoBotServicesOptions.Value.InstancePrefix) ? octoBotServicesOptions.Value.JobDatabaseName :
                $"{octoBotServicesOptions.Value.InstancePrefix}-{octoBotServicesOptions.Value.JobDatabaseName}",
            Username = systemOptions.Value.AdminUser,
            Password = systemOptions.Value.AdminUserPassword,
            AuthenticationSource = systemOptions.Value.AuthenticationDatabaseName,
            UseTls = systemOptions.Value.UseTls,
            DirectConnection = systemOptions.Value.UseDirectConnection,
            AllowInsecureTls = systemOptions.Value.AllowInsecureTls
        };

        // DatabaseHost may carry a port suffix ("host:port"). MongoServerAddress.Parse handles
        // that; the MongoServerAddress(string host) constructor rejects it since MongoDB.Driver
        // 3.11.1 (CSHARP-6171 host validation).
        if (systemOptions.Value.DatabaseHost.Contains(","))
        {
            mongoUrlBuilder.Servers = systemOptions.Value.DatabaseHost.Split(",").Select(MongoServerAddress.Parse);
        }
        else
        {
            mongoUrlBuilder.Server = MongoServerAddress.Parse(systemOptions.Value.DatabaseHost);
        }

        var mongoUrl = mongoUrlBuilder.ToString();
        var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(HangfireMongoStorageSetup));
        try
        {
            HangfireMongoStorageSetup.PrepareSchema(
                new MongoClient(mongoUrl).GetDatabase(mongoUrlBuilder.DatabaseName), storageOptions, logger);
        }
        catch (HangfireSchemaException e)
        {
            logger.LogCritical(e, "Hangfire job storage cannot start: {Message}", e.Message);
            throw;
        }

        config.UseMongoStorage(mongoUrl, storageOptions);
        config.UseNLogLogProvider();
    });

    // ReSharper disable once StringLiteralTypo
    builder.Services.AddHangfireServer(options =>
    {
        options.Queues = ["octosystem", "default"];
        // Hangfire's default poll interval is 15s, which silently floors any
        // recurring cron faster than ~15s (e.g. the per-second simulator
        // pipeline triggers) onto that grid. Default to 1s so sub-15s cron
        // expressions ('* * * * * ?') fire at their real cadence, but read it
        // from configuration so the storage/MongoDB polling load can be tuned
        // per environment without a code change.
        var schedulePollingSeconds =
            builder.Configuration.GetValue("Hangfire:SchedulePollingIntervalSeconds", 1);
        options.SchedulePollingInterval = TimeSpan.FromSeconds(schedulePollingSeconds);
    });

    // AB#5539: sweep runs a crashed/killed process left in Running are marked Failed after startup.
    builder.Services.AddOctoSecretSweepInterruptedRunRecovery();


    // NLog: Setup NLog for Dependency injection
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Trace);
    builder.Host.UseNLog();

    // Remove Kestrel request body size limit to allow large tus uploads
    builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = null);

    var app = builder.Build();

    // Ensure backup storage directories exist
    var fileStorage = app.Services.GetRequiredService<IBackupFileStorageService>();
    fileStorage.EnsureDirectoriesExist();

    app.MapObservability();

    if (app.Environment.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();
    }
    else
        // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    {
        app.UseHsts();
    }

    // Because we are behind a load balancer using HTTP, it is necessary to use XForwardProto to ensure
    // that requests are sent by HTTPS (e.g., Authentication to Identity Server)
    var forwardedHeadersOptions = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedProto,
    };
    forwardedHeadersOptions.KnownIPNetworks.Clear();
    forwardedHeadersOptions.KnownProxies.Clear();
    app.UseForwardedHeaders(forwardedHeadersOptions);

    app.UseRouting();

    // AB#7270: known origins only (shared ICorsPolicyProvider of AddOctoServiceInfrastructure()); an unknown
    // Origin gets neither Access-Control-Allow-Origin nor Access-Control-Allow-Credentials.
    app.UseCors();

    // Conversion of request query jwt token to cookie for switch from dashboard to hangfire ui dashboard.
    // AB#7270: limited to the dashboard branch; the cookie is HttpOnly, Secure, SameSite=Strict, Path=/ui.
    app.UseOctoCookieBasedAuthentication(BotServiceConstants.HangfireDashboardBranchPath);

    app.UseAuthentication();

    app.UseAuthorization();
    app.UseOctoTenantAuthorization();

    app.UseOctoApiVersioningAndDocumentation();

    // tus.io resumable upload endpoint {tenantId}/v1/tus-upload (AB#5060), stored in the artifact store so that every
    // replica serves every request of an upload (AB#8736). See TusUploadEndpoint.
    app.MapBotTusUpload(app.Services.GetRequiredService<ISharedUploadStore>(), botOptions.MaxUploadSizeBytes);

    app.MapControllers();
    // app.UseEndpoints(endpoints => { endpoints.MapControllers(); });


    var octoOptions = app.Services.GetRequiredService<IOptions<OctoBotServicesOptions>>();

    // 🔴 AB#5059 — the Hangfire dashboard is a *system* surface, not a per-user one: it lists the
    // jobs of every tenant of the instance together with their arguments (tenant ids, database
    // names, dump file names) and offers Hangfire's Delete / Requeue commands on them. It used to be
    // gated on AuthenticatedUserPolicy (RequireAuthenticatedUser) plus a dashboard filter that
    // checked nothing but IsAuthenticated — and because this host also configures an interactive
    // OpenID Connect login, *every* user of *every* tenant of the identity server could obtain such
    // a principal simply by logging in.
    //
    // It now carries the same scope requirement as JobsController, the API over the very same jobs:
    // JobApiReadOnlyPolicy to look, OctoApiFullAccess (JobApiReadWritePolicy's scope) to use the
    // mutating commands, enforced through Hangfire's own IsReadOnlyFunc.
    //
    // The scope lives on the *bearer* token — Refinery Studio passes it as ?jwt_token=, which
    // UseOctoCookieBasedAuthentication() above converts into an Authorization header and a cookie.
    // app.UseAuthentication() only runs the default (Cookies) scheme, so the branch authenticates
    // the bearer scheme explicitly first; see HangfireDashboardBearerAuthenticationMiddleware.
    //
    // 🔴 AB#4497 — on top of the scope gate the dashboard is reachable only for callers of the SYSTEM tenant
    // (the tenant_id claim of the token equals OctoSystemConfiguration.SystemTenantId) who hold the tenant role
    // TenantManagement. A tenant administrator of any other tenant would otherwise see the jobs of all tenants.
    // Other tenants and system-tenant callers without the role get 403.
    var dashboardSystemTenantId = app.Services.GetRequiredService<IOptions<OctoSystemConfiguration>>().Value
        .SystemTenantId;
    app.Map(BotServiceConstants.HangfireDashboardBranchPath, branchedApp =>
    {
        branchedApp.UseRouting();
        branchedApp.UseMiddleware<HangfireDashboardBearerAuthenticationMiddleware>();
        branchedApp.UseAuthorization(BotServiceConstants.JobApiReadOnlyPolicy);

        branchedApp.UseMiddleware<HangfireDashboardAccessMiddleware>(dashboardSystemTenantId);

        branchedApp.UseHangfireDashboard("/jobs", new DashboardOptions
        {
            AppPath = octoOptions.Value.PublicRefineryStudioUrl,
            // Read-only scope may look at the jobs but must not requeue or delete them.
            IsReadOnlyFunc = dashboardContext =>
                !HangfireDashboardScopes.HasWriteAccess(dashboardContext.GetHttpContext().User),
            Authorization = [new HangfireDashboardAuthorizationFilter(dashboardSystemTenantId)]
        });
    });

    app.UseStaticFiles();

    // Register recurring cleanup job for stale backup files
    RecurringJob.AddOrUpdate<ICleanupStaleFilesJob>("cleanup-stale-files",
        job => job.Run(BotCancellationToken.Null), Cron.Hourly);

    // AB#5539 (concept AB#5528 §5.2 phase 4): recurring Verify over all tenants, and the one-time Encrypt
    // as a never-scheduled recurring job so an operator can trigger it from the dashboard ("Trigger now")
    // besides POST system/v1/secrets/sweep. Both are owned by the system tenant (job tenant binding).
    var systemTenantId = app.Services.GetRequiredService<IOptions<OctoSystemConfiguration>>().Value.SystemTenantId;
    if (string.IsNullOrWhiteSpace(secretSweepOptions.VerifyCron))
    {
        RecurringJob.RemoveIfExists(BotServiceConstants.SecretSweepVerifyRecurringJobId);
    }
    else
    {
        RecurringJob.AddOrUpdate<ISecretSweepJob>(BotServiceConstants.SecretSweepVerifyRecurringJobId,
            job => job.RunAllTenants(systemTenantId, SecretSweepMode.Verify, SecretSweepTrigger.Recurring, null,
                null, BotCancellationToken.Null),
            secretSweepOptions.VerifyCron);
    }

    RecurringJob.AddOrUpdate<ISecretSweepJob>(BotServiceConstants.SecretSweepEncryptRecurringJobId,
        job => job.RunAllTenants(systemTenantId, SecretSweepMode.Encrypt, SecretSweepTrigger.Manual, null, null,
            BotCancellationToken.Null),
        Cron.Never());

    await app.RunAsync();
}
catch (Exception ex)
{
    //NLog: catch setup errors
    logger.Error(ex, "Stopped program because of exception");
    throw;
}
finally
{
    // Ensure to flush and stop internal timers/threads before application-exit (Avoid segmentation fault on Linux)
    LogManager.Shutdown();
}