namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
///     E-Mail Sender Job
/// </summary>
public interface IEMailSenderJob
{
    /// <summary>
    ///     Exports a runtime model
    /// </summary>
    /// <param name="tenantId">The corresponding tenant id</param>
    /// <param name="cancellationToken">An cancellation token to abort the job</param>
    /// <returns>The key the result file is stored.</returns>
    Task SendEMail(string tenantId, IBotCancellationToken? cancellationToken);
}