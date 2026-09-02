namespace Meshmakers.Octo.Backend.BotServices.Routing;

/// <summary>
///     Checks if the tenant id is a valid string.
/// </summary>
/// <remarks>
///     AB#5060 — added with the first <c>{tenantId}</c> route of this service. Every other OctoMesh
///     host that serves a tenant-addressed surface carries the identical constraint (asset-repo,
///     identity, communication-controller, mcp, ai-services); it is registered in
///     <c>Program.cs</c> under the key <c>tenantId</c> so route templates can read
///     <c>{tenantId:tenantId}</c>. The constraint itself only rejects a missing route value — the
///     tenant is authorized by <c>TenantAuthorizationMiddleware</c> and resolved by the job.
/// </remarks>
internal class TenantIdRouteConstraint : IRouteConstraint
{
    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values,
        RouteDirection routeDirection)
    {
        // check nulls
        var isMatch = values.TryGetValue(routeKey, out var tenantId) && tenantId != null;
        return isMatch;
    }
}
