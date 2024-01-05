using Hangfire.Dashboard;

namespace Meshmakers.Octo.Backend.BotServices;

internal class HangfireDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();

        return httpContext.User.Identity?.IsAuthenticated ?? false;
    }
}