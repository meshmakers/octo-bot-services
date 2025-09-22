using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Commands;

internal class ImportCkModelCommand(
    ILogger<ImportCkModelCommand> logger,
    ICkSerializer ckSerializer,
    ISystemContext systemContext)
    : CommandBase, IImportCkModelCommand
{
    public async Task ImportTextAsync(string tenantId, string jsonText,
        CancellationToken? cancellationToken = null)
    {
        try
        {
            logger.LogInformation("Reading CK model....");
            var operationResult = new OperationResult();
            var ckCompiledModelRoot = await ckSerializer.DeserializeCompiledModelRootAsync(jsonText, "-", operationResult);

            if (ckCompiledModelRoot == null)
            {
                logger.LogInformation("Import of CK model failed, model cannot be deserialized");
                operationResult.WriteMessagesToLogger(logger);
                throw CommandExecutionFailedException.CannotDeserializeModelFromString(jsonText);
            }

            logger.LogInformation("Executing import of CK model....");
            var tenantContext = await systemContext.FindTenantContextAsync(tenantId);

            await tenantContext.ImportCkModelAsync(ckCompiledModelRoot);

            logger.LogInformation("Import of CK model completed");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Import of CK model failed");
            throw;
        }
    }

    public async Task ImportAsync(string tenantId, string filePath,
        CancellationToken? cancellationToken = null)
    {
        try
        {
            logger.LogInformation("Reading CK model....");
            var operationResult = new OperationResult();
            await using var streamReader = File.OpenRead(filePath);
            var ckCompiledModelRoot =
                await ckSerializer.DeserializeCompiledModelRootAsync(streamReader, Path.GetFileName(filePath), operationResult);

            if (operationResult.HasErrors)
            {
                logger.LogError("Import of CK model failed, model cannot be deserialized");
                operationResult.WriteMessagesToLogger(logger);
                throw CommandExecutionFailedException.CannotDeserializeModel(filePath);
            }

            logger.LogInformation("Executing import of CK model....");
            var tenantContext = await systemContext.FindTenantContextAsync(tenantId);
            await tenantContext.ImportCkModelAsync(ckCompiledModelRoot);

            logger.LogInformation("Import of CK model completed");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Import of CK model failed");
            throw;
        }
    }
}