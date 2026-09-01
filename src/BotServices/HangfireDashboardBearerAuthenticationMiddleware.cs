using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Meshmakers.Octo.Backend.BotServices;

/// <summary>
///     Makes the bearer token behind the Hangfire dashboard's <c>?jwt_token=</c> entry point the
///     principal the dashboard is authorized against.
/// </summary>
/// <remarks>
///     AB#5059. Two authentication schemes reach <c>/ui/jobs</c> and only one of them can satisfy a
///     scope check:
///     <list type="bullet">
///         <item>
///             <description>
///                 The <b>bearer</b> token. Refinery Studio opens the dashboard as
///                 <c>{botServicesUri}ui/jobs?jwt_token={accessToken}</c>
///                 (<c>my-command-settings.service.ts</c>); <c>UseOctoCookieBasedAuthentication()</c>
///                 turns that query parameter into an <c>Authorization: Bearer</c> header and parks it
///                 in the <c>OctoIdentityAccessToken</c> cookie so the dashboard's own follow-up
///                 requests (assets, AJAX, paging — none of which carry the query string) present it
///                 too. That token carries the <c>scope</c> claim.
///             </description>
///         </item>
///         <item>
///             <description>
///                 The <b>cookie</b> principal from this host's interactive OpenID Connect login,
///                 which is what <c>app.UseAuthentication()</c> produces (the default scheme is
///                 Cookies). It carries <c>openid profile email role</c> only — never a <c>scope</c>
///                 claim, because scopes are an access-token property and are not returned by the
///                 userinfo endpoint. It can therefore never satisfy the job API scope requirement.
///             </description>
///         </item>
///     </list>
///     The host's <c>UseAuthentication()</c> only runs the default (cookie) scheme, so without this
///     middleware the bearer token is present in the request but never turned into a principal. It is
///     evaluated explicitly here, and only when the ambient principal does not already satisfy the
///     dashboard's read scope, so a future scope-carrying cookie principal is left alone.
/// </remarks>
internal class HangfireDashboardBearerAuthenticationMiddleware
{
    private readonly RequestDelegate _next;

    public HangfireDashboardBearerAuthenticationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task Invoke(HttpContext context)
    {
        if (!HangfireDashboardScopes.HasReadAccess(context.User))
        {
            var result = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
            if (result.Succeeded && result.Principal != null)
            {
                context.User = result.Principal;
            }
        }

        await _next(context).ConfigureAwait(false);
    }
}
