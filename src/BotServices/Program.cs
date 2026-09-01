using System.IdentityModel.Tokens.Jwt;
using BotServices.Resources;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using IdentityModel;
using Meshmakers.Octo.Backend.BotServices;
using Meshmakers.Octo.Backend.BotServices.Configuration;
using Meshmakers.Octo.Backend.BotServices.Consumers;
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
    builder.Services.AddCors();

    // AB#5032 (wired here with AB#5047): lets an operator narrow the client-credentials
    // exemption of UseOctoTenantAuthorization() per environment (OCTO_TENANTAUTHORIZATION__…).
    // The defaults reproduce the previous behaviour and only add the audit log.
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
        }).AddJwtBearer(jwt =>
        {
            jwt.Audience = CommonConstants.OctoApi;
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                NameClaimType = JwtClaimTypes.Name,
                RoleClaimType = JwtClaimTypes.Role
            };
        });


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

    // tus.io resumable upload middleware
    app.MapTus("/system/v1/tus-upload", async httpContext => new DefaultTusConfiguration
    {
        Store = new TusDiskStore(fileStorage.TusStoragePath),
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
                // Both require 'tenantId'.
                var metadata = ctx.Metadata;
                if (!metadata.ContainsKey("tenantId"))
                {
                    ctx.FailRequest("Metadata must include 'tenantId'");
                }
                else if (!metadata.ContainsKey("databaseName") && !metadata.ContainsKey("archiveRtId"))
                {
                    ctx.FailRequest("Metadata must include either 'databaseName' (restore) or 'archiveRtId' (archive data import)");
                }
            }
        }
    });

    app.MapControllers();
    // app.UseEndpoints(endpoints => { endpoints.MapControllers(); });


    var octoOptions = app.Services.GetRequiredService<IOptions<OctoBotServicesOptions>>();

    app.Map("/ui", branchedApp =>
    {
        branchedApp.UseRouting();
        branchedApp.UseAuthorization(BotServiceConstants.AuthenticatedUserPolicy);

        branchedApp.UseHangfireDashboard("/jobs", new DashboardOptions
        {
            AppPath = octoOptions.Value.PublicRefineryStudioUrl,
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