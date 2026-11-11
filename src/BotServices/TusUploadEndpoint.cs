using System.Text;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using tusdotnet;
using tusdotnet.Models;
using tusdotnet.Models.Configuration;

namespace Meshmakers.Octo.Backend.BotServices;

/// <summary>
///     The tus upload endpoint <c>{tenantId}/v1/tus-upload</c> that feeds the restore and the archive data import.
/// </summary>
public static class TusUploadEndpoint
{
    /// <summary>The route template of the endpoint.</summary>
    public const string Route = "/{tenantId:tenantId}/v1/tus-upload";

    /// <summary>
    ///     Maps the tus endpoint over <paramref name="uploads" />. Program.cs and the tests use the same mapping.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder (the web application).</param>
    /// <param name="uploads">The shared upload store (artifact store, AB#8736).</param>
    /// <param name="maxUploadSizeBytes">Largest upload accepted (<c>Bot:MaxUploadSizeBytes</c>).</param>
    public static IEndpointConventionBuilder MapBotTusUpload(this IEndpointRouteBuilder endpoints,
        ISharedUploadStore uploads, long maxUploadSizeBytes)
    {
        // The tenant of a tus request. Cannot be null: the endpoint's own route template carries {tenantId}, so a
        // request that reached the delegate matched it. Throwing rather than defaulting keeps a future route change
        // from silently pooling every tenant's uploads under one key prefix.
        static string GetTusTenantId(HttpContext httpContext)
        {
            return httpContext.GetTenantId()
                   ?? throw new InvalidOperationException(
                       "The tus upload endpoint was reached without a tenant route value.");
        }

        // tus.io resumable upload middleware.
        //
        // 🔴 The tenant is a ROUTE segment (AB#5060). It used to be `/system/v1/tus-upload` with the
        // tenant as an upload-metadata field, which meant two things: the transport tenant gate never
        // saw the request (it reads the route value), and the metadata field bound nothing, because
        // the file was stored flat under its tus file id and no consumer ever read the field back.
        // Now the gate authorizes the upload like any other tenant route, and the file is stored under
        // the tenant's own key prefix, so the binding is structural rather than declared.
        //
        // 🔴 AB#8736: the store is the artifact store (ArtifactTusStore over ISharedUploadStore), not the pod-local
        // TusDiskStore. The bot runs with more than one replica and the ingress has no affinity, so the creation, the
        // offset HEAD, every PATCH and the job start of one upload may each reach another replica.
        //
        // Endpoint routing, not the IApplicationBuilder branch: only the endpoint overload produces
        // route values, and `UseRouting()` runs above `UseOctoTenantAuthorization()` so the gate sees
        // {tenantId} by the time it looks.
        return endpoints.MapTus(Route, httpContext => Task.FromResult(new DefaultTusConfiguration
        {
            Store = new ArtifactTusStore(uploads, GetTusTenantId(httpContext)),
            MaxAllowedUploadSizeInBytesLong = maxUploadSizeBytes,
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

                    // Require the OctoApiFullAccess scope for write operations and (AB#4497) the tenant role
                    // TenantManagement of the caller, like the restore / import routes this upload feeds.
                    if (!TusUploadAuthorization.MayUpload(ctx.HttpContext.User))
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
                },
                // AB#8736: nothing happens at the completion. The upload stays in the shared store until the consuming
                // job (on any replica) reads it; copying a multi-GB file here would hold the final PATCH past the
                // ingress timeout.
            }
        }))
        // The consuming restore / import routes carry the same marker, so a parent administrator
        // securing a child tenant can still stage the file it will restore. Without it the upload
        // would refuse the exact caller the restore then accepts.
        .WithMetadata(new AllowParentTenantAdministrationAttribute());
    }
}
