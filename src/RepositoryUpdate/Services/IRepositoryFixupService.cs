namespace RepositoryUpdate.Services;

public interface IRepositoryFixupService
{
    Task FixupRepositoryAsync(string tenantId, CancellationToken? cancellationToken = null);
}