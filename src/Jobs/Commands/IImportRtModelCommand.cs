namespace Meshmakers.Octo.Backend.Jobs.Commands;

/// <summary>
/// Interface for importing a runtime model from a file.
/// </summary>
public interface IImportRtModelCommand
{
    /// <summary>
    /// Imports as text
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="jsonText"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task ImportText(string tenantId, string jsonText, CancellationToken? cancellationToken = null);
    
    /// <summary>
    /// Imports from a file
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="filePath"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task Import(string tenantId, string filePath, CancellationToken? cancellationToken = null);
}