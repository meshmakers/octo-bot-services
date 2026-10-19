using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

namespace Meshmakers.Octo.Backend.Jobs.Commands;

/// <summary>
///     Builds the security context an export job runs with (AB#6734): the identity and roles of the user (or
///     client) that requested the export, so the engine applies that caller's data permissions.
/// </summary>
/// <remarks>
///     Fail closed: a request without a subject id (an older producer, a tampered message) yields no context, and
///     the export is refused. The system context is never a substitute, because it bypasses every data permission.
/// </remarks>
public static class ExportCallerContext
{
    /// <summary>
    ///     Creates the user context of the caller, or throws when the request carries no caller identity.
    /// </summary>
    /// <param name="tenantId">Tenant of the export (for the message)</param>
    /// <param name="exportKind">Kind of export (for the message)</param>
    /// <param name="subjectId">Subject id (user <c>sub</c> or client id) of the requesting caller</param>
    /// <param name="roleNames">Role names of the requesting caller</param>
    public static RtSecurityContext Require(string tenantId, string exportKind, string? subjectId,
        IEnumerable<string>? roleNames)
    {
        if (string.IsNullOrWhiteSpace(subjectId))
        {
            throw JobFailedException.ExportCallerContextMissing(tenantId, exportKind);
        }

        return RtSecurityContext.ForUser(subjectId, roleNames);
    }

    /// <summary>
    ///     True when the context may be used for an export session: an identified user context, never the system.
    /// </summary>
    internal static bool IsUsable(RtSecurityContext? securityContext)
    {
        return securityContext is { IsSystem: false } && !string.IsNullOrWhiteSpace(securityContext.SubjectId);
    }

    /// <summary>
    ///     Opens the repository session of an export for the caller. Fails closed: the system context is refused,
    ///     and so is a repository that cannot open a caller-scoped session - the
    ///     <c>GetSessionAsync(RtSecurityContext)</c> extension would silently fall back to a system session there,
    ///     which would export unfiltered.
    /// </summary>
    internal static async Task<IOctoSession> OpenSessionAsync(ITenantRepository tenantRepository,
        RtSecurityContext securityContext, string tenantId, string exportKind)
    {
        if (!IsUsable(securityContext))
        {
            throw JobFailedException.ExportCallerContextMissing(tenantId, exportKind);
        }

        if (tenantRepository is not ISecureSessionFactory sessionFactory)
        {
            throw new JobFailedException(
                $"The {exportKind} export for tenant '{tenantId}' is refused: the tenant repository cannot open a " +
                "session for the requesting caller, and an export is never run as the system.");
        }

        return await sessionFactory.GetSessionAsync(securityContext);
    }
}
