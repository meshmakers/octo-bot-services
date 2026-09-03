using System.IdentityModel.Tokens.Jwt;
using System.Text;
using BotServices.Resources;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using IdentityModel;
using Meshmakers.Octo.Backend.BotServices;
using Meshmakers.Octo.Backend.BotServices.Configuration;
using Meshmakers.Octo.Backend.BotServices.Consumers;
using Meshmakers.Octo.Backend.BotServices.Routing;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Extensions;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Observability;
using Meshmakers.Octo.Services.Swagger.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using NLog;
using NLog.Web;
using tusdotnet;
using tusdotnet.Models;
using tusdotnet.Models.Configuration;
using tusdotnet.Stores;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

// NLog: set up the logger first to catch all errors
var nLogFactory = LogManager.Setup().RegisterNLogWeb().LoadConfigurationFromFile("nlog.config").LogFactory;
var logger = nLogFactory.GetCurrentClassLogger();


try
{
    logger.Debug("init main");

    var builder = WebApplication.CreateBuilder(args);
    builder.AddObservability()
        .AddSystemContextHealthCheck();

    JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

    builder.Services.Configure<OctoBotServicesOptions>(options =>
        builder.Configuration.GetSection("Bot").Bind(options));
    builder.Services.Configure<OctoSystemConfiguration>(options =>
        builder.Configuration.GetSection("System").Bind(options));

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

    // AB#5060: this service now serves a tenant-addressed surface as well
    // ({tenantId}/v1/jobs/dump-repository, restore-from-upload, export-archive-data,
    // import-archive-data-from-upload, run-fixup-scripts), so the `tenantId` route constraint every
    // other tenant-serving OctoMesh host registers is needed here too. Without it the
    // `{tenantId:tenantId}` templates of TenantApi never match and the routes 404.
    builder.Services.Configure<RouteOptions>(options =>
        options.ConstraintMap.Add("tenantId", typeof(TenantIdRouteConstraint)));

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

            c.AddCommandConsumer<ModelCommandsConsumer, ImportCkCommandRequest>(QueueNames.ImportCkCommand);
            c.AddCommandConsumer<ModelCommandsConsumer, ImportCkBatchCommandRequest>(QueueNames.ImportCkBatchCommand);
            c.AddCommandConsumer<ModelCommandsConsumer, ImportRtCommandRequest>(QueueNames.ImportRtCommand);
            c.AddCommandConsumer<ModelCommandsConsumer, ExportRtByQueryCommandRequest>(
                QueueNames.ExportRtByQueryCommand);
            c.AddCommandConsumer<ModelCommandsConsumer, ExportRtByDeepGraphCommandRequest>(QueueNames
                .ExportRtByDeepGraphCommand);
            c.AddCommandConsumer<RecurringJobConsumer, RemoveRecurringJobsByScheduleGroupRequest>(QueueNames
                .RemoveRecurringJobsByScheduleGroupCommand);
            c.AddCommandClient<CreateIdentityDataCommandRequest>(QueueNames.CreateIdentityDataCommand);
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PosCreateTenant>();
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PosUpdateTenant>();
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PreDeleteTenant>();
        });

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

    builder.Services.AddOctoJobs(
        tusStoragePath: botOptions.TusStoragePath,
        dumpStoragePath: botOptions.DumpStoragePath,
        fileRetentionHours: botOptions.FileRetentionHours);
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
            options.Cookie.SameSite = SameSiteMode.None;
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


    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy(BotServiceConstants.AuthenticatedUserPolicy,
            policyBuilder => policyBuilder.RequireAuthenticatedUser());

        options.AddPolicy(BotServiceConstants.JobApiReadOnlyPolicy, authorizationPolicyBuilder =>
        {
            authorizationPolicyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                CommonConstants.OctoApiFullAccess,
                CommonConstants.OctoApiReadOnly);
        });

        options.AddPolicy(BotServiceConstants.JobApiReadWritePolicy, authorizationPolicyBuilder =>
        {
            authorizationPolicyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                CommonConstants.OctoApiFullAccess);
        });
    });

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
            { BotServiceConstants.JobApiReadWritePolicy, [CommonConstants.OctoApiFullAccess] }
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

        var storageOptions = new MongoStorageOptions
        {
            MigrationOptions = new MongoMigrationOptions
            {
                MigrationStrategy = new DropMongoMigrationStrategy(),
                BackupStrategy = new NoneMongoBackupStrategy()
            }
        };
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

        if (systemOptions.Value.DatabaseHost.Contains(","))
        {
            mongoUrlBuilder.Servers =
                systemOptions.Value.DatabaseHost.Split(",").Select(x => new MongoServerAddress(x));
        }
        else
        {
            mongoUrlBuilder.Server = new MongoServerAddress(systemOptions.Value.DatabaseHost);
        }

        config.UseMongoStorage(mongoUrlBuilder.ToString(), storageOptions);
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

    app.UseCors(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()
        .WithExposedHeaders("Upload-Offset", "Upload-Length", "Tus-Resumable", "Location")
        .SetPreflightMaxAge(TimeSpan.FromHours(1)));

    // Conversion of request query jwt token to cookie for switch from dashboard to hangfire ui dashboard
    app.UseOctoCookieBasedAuthentication();

    app.UseAuthentication();

    app.UseAuthorization();
    app.UseOctoTenantAuthorization();

    app.UseOctoApiVersioningAndDocumentation();

    // Resolves and creates the tenant's own upload directory for this request. Called per tus
    // request because the tenant is per request; TusDiskStore is a thin wrapper over a path, so
    // building one each time costs nothing.
    string EnsureTenantUploadDirectory(HttpContext httpContext)
    {
        // Cannot be null: the endpoint's own route template carries {tenantId}, so a request that
        // reached this delegate matched it. Throwing rather than defaulting keeps a future route
        // change from silently pooling every tenant's uploads in one directory again.
        var tenantId = httpContext.GetTenantId()
                       ?? throw new InvalidOperationException(
                           "The tus upload endpoint was reached without a tenant route value.");

        var directory = fileStorage.GetTusUploadDirectory(tenantId);
        Directory.CreateDirectory(directory);
        return directory;
    }

    // tus.io resumable upload middleware.
    //
    // 🔴 The tenant is a ROUTE segment (AB#5060). It used to be `/system/v1/tus-upload` with the
    // tenant as an upload-metadata field, which meant two things: the transport tenant gate never
    // saw the request (it reads the route value), and the metadata field bound nothing, because
    // the file was stored flat under its tus file id and no consumer ever read the field back.
    // Now the gate authorizes the upload like any other tenant route, and the file is stored under
    // the tenant's own directory, so the binding is structural rather than declared.
    //
    // Endpoint routing, not the IApplicationBuilder branch: only the endpoint overload produces
    // route values, and `UseRouting()` runs above `UseOctoTenantAuthorization()` so the gate sees
    // {tenantId} by the time it looks.
    app.MapTus("/{tenantId:tenantId}/v1/tus-upload", async httpContext => new DefaultTusConfiguration
    {
        Store = new TusDiskStore(EnsureTenantUploadDirectory(httpContext)),
        MaxAllowedUploadSizeInBytesLong = botOptions.MaxUploadSizeBytes,
        Events = new Events
        {
            OnAuthorizeAsync = async ctx =>
            {
                // The default auth scheme is Cookies, so we must explicitly
                // authenticate with JWT Bearer for API calls with Bearer tokens.
                var authResult = await ctx.HttpContext.AuthenticateAsync(
                    JwtBearerDefaults.AuthenticationScheme);
                if (authResult.Succeeded)
                {
                    ctx.HttpContext.User = authResult.Principal!;
                }

                if (ctx.HttpContext.User.Identity is not { IsAuthenticated: true })
                {
                    ctx.FailRequest(System.Net.HttpStatusCode.Unauthorized);
                    return;
                }

                // Require the OctoApiFullAccess scope for write operations
                var hasClaim = ctx.HttpContext.User.HasClaim(
                    InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess);
                if (!hasClaim)
                {
                    ctx.FailRequest(System.Net.HttpStatusCode.Forbidden);
                }
            },
            OnBeforeCreateAsync = async ctx =>
            {
                // Validate required metadata. The same TUS endpoint serves two upload flows:
                //   - tenant restore       → requires 'databaseName'
                //   - archive data import  → requires 'archiveRtId' (AB#4230)
                var metadata = ctx.Metadata;
                if (!metadata.ContainsKey("databaseName") && !metadata.ContainsKey("archiveRtId"))
                {
                    ctx.FailRequest("Metadata must include either 'databaseName' (restore) or 'archiveRtId' (archive data import)");
                    return;
                }

                // 'tenantId' metadata is accepted for older clients but the ROUTE decides. Refuse a
                // disagreement rather than silently preferring one: a client that sends a different
                // tenant than it addressed has a bug, and quietly honouring the route would hide it
                // until a restore ran against the wrong database. Refusing costs nothing — every
                // current client derives both from the same value.
                if (metadata.TryGetValue("tenantId", out var declaredTenant))
                {
                    var routeTenant = ctx.HttpContext.GetTenantId();
                    var declared = declaredTenant.GetString(Encoding.UTF8);
                    if (!string.Equals(declared, routeTenant, StringComparison.Ordinal))
                    {
                        ctx.FailRequest(
                            $"Upload metadata names tenant '{declared}' but the request addresses '{routeTenant}'.");
                    }
                }
            }
        }
    })
    // The consuming restore / import routes carry the same marker, so a parent administrator
    // securing a child tenant can still stage the file it will restore. Without it the upload
    // would refuse the exact caller the restore then accepts.
    .WithMetadata(new AllowParentTenantAdministrationAttribute());

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
    app.Map("/ui", branchedApp =>
    {
        branchedApp.UseRouting();
        branchedApp.UseMiddleware<HangfireDashboardBearerAuthenticationMiddleware>();
        branchedApp.UseAuthorization(BotServiceConstants.JobApiReadOnlyPolicy);

        branchedApp.UseHangfireDashboard("/jobs", new DashboardOptions
        {
            AppPath = octoOptions.Value.PublicRefineryStudioUrl,
            // Read-only scope may look at the jobs but must not requeue or delete them.
            IsReadOnlyFunc = dashboardContext =>
                !HangfireDashboardScopes.HasWriteAccess(dashboardContext.GetHttpContext().User),
            Authorization = [new HangfireDashboardAuthorizationFilter()]
        });
    });

    app.UseStaticFiles();

    // Register recurring cleanup job for stale backup files
    RecurringJob.AddOrUpdate<ICleanupStaleFilesJob>("cleanup-stale-files",
        job => job.Run(BotCancellationToken.Null), Cron.Hourly);

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