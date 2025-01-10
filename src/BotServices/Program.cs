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
using Meshmakers.Octo.Backend.BotServices.Hangfire;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Extensions;
using Meshmakers.Octo.Services.Common;
using Meshmakers.Octo.Services.Common.Authorization;
using Meshmakers.Octo.Services.Common.Cors;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Observability;
using Meshmakers.Octo.Services.Swagger.Configuration;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
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
        .AddSystemContextHealthCheck();

    JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

    builder.Services.Configure<OctoBotServicesOptions>(options =>
        builder.Configuration.GetSection("Bot").Bind(options));
    builder.Services.Configure<OctoSystemConfiguration>(options =>
        builder.Configuration.GetSection("System").Bind(options));

    builder.Services.AddSingleton<CorsPolicyProvider>();
    builder.Services.AddSingleton<ICorsPolicyProvider>(provider => provider.GetRequiredService<CorsPolicyProvider>());

    builder.Services.AddTransient<IJobCreatorService, JobCreatorService>();
    builder.Services.AddCors();

    builder.Services.AddScoped<IDefaultConfigurationCreatorService, DefaultConfigurationCreatorService>();

    builder.Services.AddTransient<IImportModelJob, ImportModelJob>();
    builder.Services.AddTransient<IExportModelJob, ExportModelJob>();
    builder.Services.AddTransient<IServiceHookJob, ServiceHookJob>();
    builder.Services.AddTransient<IAttributeValueAggregatorJob, AttributeValueAggregatorJob>();

    builder.Services.AddMemoryCache();

    builder.Services.AddOctoServiceInfrastructure("BotService",
        c =>
        {
            c.AddHangfireMessageScheduler();

            c.AddCommandConsumer<ModelCommandsConsumer, ImportCkCommandRequest>(QueueNames.ImportCkCommand);
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
        .AddMongoDbRuntimeRepository();

    builder.Services.AddOctoCommands();
    builder.Services.AddOctoNotification();

    builder.Services.ConfigureOptions<ConfigureIdentityServerAuthenticationOptions>();
    builder.Services.ConfigureOptions<ConfigureOpenIdConnectOptions>();
    builder.Services.ConfigureOptions<ConfigureOctoOpenApiOptions>();
    builder.Services.ConfigureOptions<ConfigureDistributionEventHubOptions>();

    builder.Services.AddAuthentication(authenticationOptions =>
        {
            authenticationOptions.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            authenticationOptions.DefaultChallengeScheme = BackendCommon.OidcAuthenticationScheme;
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
        .AddOpenIdConnect(BackendCommon.OidcAuthenticationScheme, options =>
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
            jwt.Audience = CommonConstants.BotApi;
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
            // require SystemApiFullAccess or SystemApiReadOnly
            authorizationPolicyBuilder.RequireClaim(BackendCommon.ClaimScope, CommonConstants.BotApiFullAccess,
                CommonConstants.BotApiReadOnly);
        });

        options.AddPolicy(BotServiceConstants.JobApiReadWritePolicy, authorizationPolicyBuilder =>
        {
            // require SystemApiFullAccess
            authorizationPolicyBuilder.RequireClaim(BackendCommon.ClaimScope, CommonConstants.BotApiFullAccess);
        });
    });

    builder.Services.AddMvcCore().AddAuthorization();
    builder.Services.AddMvc();

    builder.Services.AddOctoApiVersioningAndDocumentation(options =>
    {
        options.Scopes = new Dictionary<string, string>
        {
            {
                CommonConstants.BotApiFullAccess,
                BotTexts.Backend_BotServices_Api_FullAccess
            },
            {
                CommonConstants.BotApiReadOnly,
                BotTexts.Backend_BotServices_Api_ReadOnlyAccess
            }
        };

        options.PolicyScopeMapping = new Dictionary<string, IEnumerable<string>>
        {
            { BotServiceConstants.JobApiReadOnlyPolicy, [CommonConstants.BotApiReadOnly] },
            { BotServiceConstants.JobApiReadWritePolicy, [CommonConstants.BotApiFullAccess] }
        };
        
        options.XmlDocDataTransferObjectAssemblies = [typeof(JobDto).Assembly];
        options.XmlDocOperationAssemblies = [typeof(Program).Assembly];

        options.ApiTitle = "Octo Services API";
        options.ApiDescription = "Octo Mesh Bot builder.Services.";

        options.ClientId = CommonConstants.OctoBotServicesSwaggerClientId;
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
            DatabaseName = octoBotServicesOptions.Value.JobDatabaseName,
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
        config.UseLogProvider(new NLogProvider());
    });

    // ReSharper disable once StringLiteralTypo
    builder.Services.AddHangfireServer(options => { options.Queues = ["octosystem", "default"]; });


    // NLog: Setup NLog for Dependency injection
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Trace);
    builder.Host.UseNLog();

    // additional providers here needed.
    // allow environment variables to override values from other providers.
    builder.Configuration.AddEnvironmentVariables("OCTO_").AddCommandLine(args)
        .AddUserSecrets(typeof(Program).Assembly, true);


    var app = builder.Build();

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

    app.UseRouting();

    app.UseCors();

    // Conversion of request query jwt token to cookie for switch from dashboard to hangfire ui dashboard
    app.UseMiddleware<CookieBasedAuthorizationMiddleware>();

    app.UseAuthentication();

    app.UseAuthorization();

    app.UseOctoApiVersioningAndDocumentation();

    // Because we are behind a load balancer using HTTP it is needed to use XForwardProto to ensure
    // that requests are send by HTTPS (e. g. Authentication to Identity Server)
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedProto
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
            AppPath = octoOptions.Value.PublicAdminPanelUrl,
            Authorization = new[] { new HangfireDashboardAuthorizationFilter() }
        });
    });

    app.UseStaticFiles();

    app.Run();
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