namespace Meshmakers.Octo.Backend.Jobs.Commands;

/// <summary>
///     Import a construction kit model from a file.
/// </summary>
public interface IImportCkModelCommand
{
    /// <summary>
    ///     Imports as text
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="jsonText"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task ImportTextAsync(string tenantId, string jsonText,
        CancellationToken? cancellationToken = null);

    /// <summary>
    ///     Imports from a file
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="filePath"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task ImportAsync(string tenantId, string filePath,
        CancellationToken? cancellationToken = null);
}