using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using BotServices.Resources;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using IdentityModel;
using Meshmakers.Octo.Backend.BotServices.Configuration;
using Meshmakers.Octo.Backend.BotServices.Hangfire;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.Common;
using Meshmakers.Octo.Backend.Common.Authorization;
using Meshmakers.Octo.Backend.DistributedCache;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Backend.Swagger.Configuration;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.Jobs;
using Meshmakers.Octo.Common.Shared.Services;
using Meshmakers.Octo.Services.Common.Cors;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.Configuration;
using Meshmakers.Octo.SystematizedData.Persistence.SystemStores;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.BotServices;

/// <summary>
///     OWIN startup class implementation
/// </summary>
public class Startup
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="configuration"></param>
    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    private IConfiguration Configuration { get; }

    /// <summary>
    ///     This method gets called by the runtime. Use this method to add services to the container.
    ///     For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
    /// </summary>
    /// <param name="services"></param>
    public void ConfigureServices(IServiceCollection services)
    {
        JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

        services.Configure<OctoBotServicesOptions>(options =>
            Configuration.GetSection("Bot").Bind(options));
        services.Configure<OctoSystemConfiguration>(options => Configuration.GetSection("System").Bind(options));
        services.Configure<EMailOptions>(options => Configuration.GetSection("EMail").Bind(options));

        services.AddHostedService<StartupService>();

        services.AddTransient<IOctoClientStore, ClientStore>();
        services.AddTransient<IOctoResourceStore, ResourceStore>();
        services.AddSingleton<ICorsPolicyProvider, CorsPolicyProvider>();
        services.AddSingleton<INotificationRepository, EntityNotificationRepository>();
        services.AddSingleton<IEMailSender, EMailSender>();
        services.AddCors();

        services.AddSingleton<ISystemContext, SystemContext>();

        services.AddTransient<IUserSchemaService, UserSchemaService>();
        services.AddTransient<IServiceHookService, ServiceHookService>();
        services.AddTransient<IImportModelJob, ImportModelJob>();
        services.AddTransient<IExportModelJob, ExportModelJob>();
        services.AddTransient<IEMailSenderJob, EMailSenderJob>();
        services.AddTransient<IServiceHookJob, ServiceHookJob>();
        services.AddTransient<IAttributeValueAggregatorJob, AttributeValueAggregatorJob>();

        services.AddDistributedPubSubCache();
        services.AddMemoryCache();

        services.ConfigureOptions<ConfigureIdentityServerAuthenticationOptions>();
        services.ConfigureOptions<ConfigureOpenIdConnectOptions>();
        services.ConfigureOptions<ConfigureOctoSwaggerOptions>();
        services.ConfigureOptions<ConfigureDistributeCacheWithPubSubOptions>();

        services.AddAuthentication(authenticationOptions =>
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


        services.AddAuthorization(options =>
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

        services.AddMvcCore().AddAuthorization();
        services.AddMvc();

        services.AddOctoApiVersioningAndDocumentation(options =>
        {
            options.AddXmlDocAssembly<Startup>();
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

            options.ApiTitle = "Octo Services API";
            options.ApiDescription = "Octo Mesh Bot Services.";

            options.ClientId = CommonConstants.OctoBotServicesSwaggerClientId;
            options.AppName = BotTexts.Backend_BotServices_UserSchema_Swagger_DisplayName;
        });

        // Hangfire is used to handle background jobs and scheduled jobs
        services.AddHangfire((serviceProvider, config) =>
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
            config.UseActivator(new OctoJobActivator(serviceProvider));
        });

        services.AddHangfireServer(options => { options.Queues = new[] { "octoSystem", "default" }; });
    }

    /// <summary>
    ///     This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
    /// </summary>
    /// <param name="app"></param>
    /// <param name="env"></param>
    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
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

        app.UseOctoPersistence();
        app.UseOctoApiVersioningAndDocumentation();

        // Because we are behind a load balancer using HTTP it is needed to use XForwardProto to ensure
        // that requests are send by HTTPS (e. g. Authentication to Identity Server)
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedProto
        });

        app.UseEndpoints(endpoints => { endpoints.MapControllers(); });

        app.UseHttpsRedirection();

        var octoOptions = app.ApplicationServices.GetRequiredService<IOptions<OctoBotServicesOptions>>();

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
    }
}
