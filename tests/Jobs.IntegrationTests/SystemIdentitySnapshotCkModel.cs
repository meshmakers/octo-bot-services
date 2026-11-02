using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;

namespace Meshmakers.Octo.Backend.Jobs.IntegrationTests;

/// <summary>
///     The real <c>System.Identity</c> CK model as an embedded model of the test engine. The bot services do not ship
///     System.Identity (the identity service owns it), so the credential guard tests load the compiler output of a released
///     version from <c>testData/ck-models/</c> (provenance and refresh: README.md there). What this model contributes is the
///     real access matrix of the user: Hidden <c>PasswordHash</c>, <c>SecurityStamp</c> and <c>UserTokens</c>.
/// </summary>
internal sealed class SystemIdentitySnapshotCkModel(ICkYamlSerializer ckYamlSerializer) : ICkEmbeddedModel
{
    /// <summary>The model version of the snapshot.</summary>
    public const string ModelFullName = "System.Identity-2.30.0";

    private static readonly string SnapshotPath =
        Path.Combine(AppContext.BaseDirectory, "testData", "ck-models", "ck-system.identity-2.30.0.yaml");

    public CkModelId ModelId => new(ModelFullName);

    public string Description => "System.Identity (test snapshot of the identity service's CK model)";

    public async Task<CkCompiledModelRoot> GetCompiledModelRootAsync(OperationResult operationResult)
    {
        await using var stream = File.OpenRead(SnapshotPath);
        return await ckYamlSerializer.DeserializeCompiledModelRootAsync(stream, SnapshotPath, operationResult);
    }
}
