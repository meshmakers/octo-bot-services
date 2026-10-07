namespace Meshmakers.Octo.Backend.Jobs.Secrets;

/// <summary>
///     Keeps the last secret sweep report of every tenant (AB#5539) so the system API, octo-cli
///     (<c>SecretStatus</c>) and the Studio admin UI can read it without searching job histories.
/// </summary>
public interface ISecretSweepReportStore
{
    /// <summary>
    ///     Stores <paramref name="report" /> as the last report of its tenant.
    /// </summary>
    Task SaveAsync(SecretSweepReport report);

    /// <summary>
    ///     Returns the last report of <paramref name="tenantId" />, or <c>null</c>.
    /// </summary>
    Task<SecretSweepReport?> GetLastAsync(string tenantId);

    /// <summary>
    ///     Returns the last report of every tenant that has one, ordered by tenant id.
    /// </summary>
    Task<IReadOnlyList<SecretSweepReport>> GetAllLastAsync();
}
