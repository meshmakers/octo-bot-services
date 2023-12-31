using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Commands;

internal class ImportCkModelCommand : IImportCkModelCommand
{
    private readonly ICkSerializer _ckSerializer;
    private readonly ILogger<ImportCkModelCommand> _logger;
    private readonly ISystemContext _systemContext;

    public ImportCkModelCommand(ILogger<ImportCkModelCommand> logger, ICkSerializer ckSerializer,
        ISystemContext systemContext)
    {
        _logger = logger;
        _ckSerializer = ckSerializer;
        _systemContext = systemContext;
    }

    public async Task ImportTextAsync(string tenantId, string jsonText,
        CancellationToken? cancellationToken = null)
    {
        try
        {
            _logger.LogInformation("Reading CK model....");
            var operationResult = new OperationResult();
            var ckCompiledModelRoot = await _ckSerializer.DeserializeCompiledModelRootAsync(jsonText, "-", operationResult);

            if (ckCompiledModelRoot == null)
            {
                _logger.LogInformation("Import of CK model failed, model cannot be deserialized");
                operationResult.WriteMessagesToLogger(_logger);
                throw CommandExecutionFailedException.CannotDeserializeModelFromString(jsonText);
            }

            _logger.LogInformation("Executing import of CK model....");
            using var session = await _systemContext.GetSystemSessionAsync();
            session.StartTransaction();
            var tenantContext = await _systemContext.GetChildTenantContextAsync(session, tenantId);
            await session.CommitTransactionAsync();

            await tenantContext.ImportCkModelAsync(session, ckCompiledModelRoot);

            _logger.LogInformation("Import of CK model completed");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Import of CK model failed");
            throw;
        }
    }

    public async Task ImportAsync(string tenantId, string filePath,
        CancellationToken? cancellationToken = null)
    {
        try
        {
            _logger.LogInformation("Reading CK model....");
            var operationResult = new OperationResult();
            await using var streamReader = File.OpenRead(filePath);
            var ckCompiledModelRoot =
                await _ckSerializer.DeserializeCompiledModelRootAsync(streamReader, Path.GetFileName(filePath), operationResult);

            if (ckCompiledModelRoot == null || operationResult.HasErrors)
            {
                _logger.LogError("Import of CK model failed, model cannot be deserialized");
                operationResult.WriteMessagesToLogger(_logger);
                throw CommandExecutionFailedException.CannotDeserializeModel(filePath);
            }

            _logger.LogInformation("Executing import of CK model....");
            using var session = await _systemContext.GetSystemSessionAsync();
            session.StartTransaction();
            var tenantContext = await _systemContext.GetChildTenantContextAsync(session, tenantId);
            await session.CommitTransactionAsync();
            await tenantContext.ImportCkModelAsync(session, ckCompiledModelRoot);

            _logger.LogInformation("Import of CK model completed");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Import of CK model failed");
            throw;
        }
    }
}