using Meshmakers.Octo.ConstructionKit.Contracts;

namespace RepositoryUpdate;

public class RepositoryUpdateException : Exception
{
    public RepositoryUpdateException()
    {
    }

    public RepositoryUpdateException(string message) : base(message)
    {
    }

    public RepositoryUpdateException(string message, Exception inner) : base(message, inner)
    {
    }

    public static Exception UpdateScriptFailed(OctoObjectId rtFixupRtId, Exception exception)
    {
        return new RepositoryUpdateException(
            $"Failed to execute repository update script for {rtFixupRtId}. See inner exception for details.", exception);
    }

    public static Exception TenantContextNotFound(string tenantId)
    {
        return new RepositoryUpdateException(
            $"Tenant context for tenant {tenantId} not found. Please ensure the tenant exists and is properly configured.");
    }
}