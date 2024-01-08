using System.Collections.Concurrent;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Commands;

internal class ImportRtModelCommand : IImportRtModelCommand
{
    private readonly HashSet<OctoObjectId> _entityImportIds;
    private readonly ConcurrentQueue<RtAssociation> _importAssociationQueue;

    private readonly ConcurrentQueue<RtEntity> _importEntityQueue;
    private readonly ILogger<ImportRtModelCommand> _logger;
    private readonly IRtSerializer _rtSerializer;
    private readonly ISystemContext _systemContext;
    private int _associationsCount;

    public ImportRtModelCommand(ILogger<ImportRtModelCommand> logger, ISystemContext systemContext, IRtSerializer rtSerializer)
    {
        _logger = logger;
        _systemContext = systemContext;
        _rtSerializer = rtSerializer;

        _entityImportIds = new HashSet<OctoObjectId>();
        _importEntityQueue = new ConcurrentQueue<RtEntity>();
        _importAssociationQueue = new ConcurrentQueue<RtAssociation>();
    }

    public async Task ImportText(string tenantId, string jsonText, CancellationToken? cancellationToken = null)
    {
        _logger.LogInformation("Importing RT entities using text started");

        using var systemSession = await _systemContext.GetSystemSessionAsync();
        systemSession.StartTransaction();
        ITenantContext tenantContext = _systemContext;
        if (tenantId != _systemContext.TenantId)
        {
            tenantContext = await _systemContext.GetChildTenantContextAsync(tenantId);
        } 
        await systemSession.CommitTransactionAsync();

        var tenantRepository = tenantContext.GetTenantRepository();

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            OperationResult operationResult = new();
            var rtModelRoot = await _rtSerializer.DeserializeAsync(jsonText, "-", operationResult);
            await ImportEntityAsync(session, rtModelRoot.Entities, tenantRepository);

            _logger.LogInformation("{Count} entities, {AssociationsCount} associations imported", _entityImportIds.Count,
                _associationsCount);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Import of RT model failed");
            throw;
        }
    }

    public async Task Import(string tenantId, string filePath, CancellationToken? cancellationToken = null)
    {
        _logger.LogInformation("Importing RT entities using file started");

        var session = await _systemContext.GetSystemSessionAsync();
        try
        {
            session.StartTransaction();
            ITenantContext tenantContext = _systemContext;
            if (tenantId != _systemContext.TenantId)
            {
                tenantContext = await _systemContext.GetChildTenantContextAsync(tenantId);
            }   
            var tenantRepository = tenantContext.GetTenantRepository();

            await using (var stream = File.OpenRead(filePath))
            {
                var rtDeserializeStream = await _rtSerializer.DeserializeStreamAsync(stream, cancellationToken);
                rtDeserializeStream.BulkDeserialized += async (_, args) =>
                {
                    await ImportEntityAsync(session, args.DeserializedEntities, tenantRepository);

                    args.IsHandled = true;
                };
                await rtDeserializeStream.ReadAsync();
            }

            _logger.LogInformation("{Count} entities, {AssociationsCount} associations imported", _entityImportIds.Count,
                _associationsCount);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Import of RT model failed");
            throw;
        }
    }

    private async Task ImportEntityAsync(IOctoSession session, IEnumerable<RtEntityDto> modelRtEntities,
        ITenantRepository tenantRepository)
    {
        await Parallel.ForEachAsync(modelRtEntities, async (modelRtEntity, token) =>
        {
            var entityCacheItem = await tenantRepository.GetCkTypeGraphAsync(modelRtEntity.CkTypeId);

            var rtEntity = await tenantRepository.CreateTransientRtEntityAsync(modelRtEntity.CkTypeId).ConfigureAwait(false);
            rtEntity.RtId = modelRtEntity.RtId;
            rtEntity.RtChangedDateTime = modelRtEntity.RtChangedDateTime;
            rtEntity.RtCreationDateTime = modelRtEntity.RtCreationDateTime;
            rtEntity.RtWellKnownName = modelRtEntity.RtWellKnownName;

            if (_entityImportIds.Contains(rtEntity.RtId))
            {
                _logger.LogError("'{RtEntityRtId}' already imported", rtEntity.RtId);
            }

            lock (_entityImportIds)
            {
                _entityImportIds.Add(rtEntity.RtId);
            }

            token.ThrowIfCancellationRequested();

            foreach (var modelAttribute in modelRtEntity.Attributes)
            {
                var attributeCacheItem =
                    entityCacheItem.AllAttributes.Values.FirstOrDefault(a => a.CkAttributeId.Equals(modelAttribute.Id));
                if (attributeCacheItem == null)
                {
                    _logger.LogError("'{ModelAttributeId}' does not exit on type '{CkTypeId}'", modelAttribute.Id,
                        entityCacheItem.CkTypeId);
                    throw CommandExecutionFailedException.AttributeNotFound(modelAttribute.Id,
                        entityCacheItem.CkTypeId);
                }

                rtEntity.SetAttributeValue(attributeCacheItem.AttributeName, attributeCacheItem.ValueType,
                    modelAttribute.Value);
            }

            _importEntityQueue.Enqueue(rtEntity);

            if (modelRtEntity.Associations != null && modelRtEntity.Associations.Count > 0)
            {
                var originId = rtEntity.RtId;

                foreach (var association in modelRtEntity.Associations)
                {
                    var rtAssociation = new RtAssociation
                    {
                        AssociationRoleId = association.RoleId,
                        OriginRtId = originId,
                        OriginCkTypeId = rtEntity.CkTypeId,
                        TargetRtId = association.TargetRtId,
                        TargetCkTypeId = association.TargetCkTypeId
                    };
                    _importAssociationQueue.Enqueue(rtAssociation);
                    Interlocked.Increment(ref _associationsCount);
                }
            }
        });

        _logger.LogInformation("{EntityCount} entities (total imports of {Count}) imported", _importEntityQueue.Count,
            _entityImportIds.Count);
        await ImportToDatabase(session, tenantRepository);
    }

    private async Task ImportToDatabase(IOctoSession session, ITenantRepository tenantRepository)
    {
        _logger.LogInformation("Importing {Count} to database", _importEntityQueue.Count);

        try
        {
            var importEntities = new List<RtEntity>();
            var importAssociations = new List<RtAssociation>();

            var entityMax = _importEntityQueue.Count;
            var associationsMax = _importAssociationQueue.Count;

            for (var i = 0; i < entityMax; i++)
            {
                if (_importEntityQueue.TryDequeue(out var tmp))
                {
                    importEntities.Add(tmp);
                }
                else
                {
                    break;
                }
            }

            for (var i = 0; i < associationsMax; i++)
            {
                if (_importAssociationQueue.TryDequeue(out var tmp))
                {
                    importAssociations.Add(tmp);
                }
                else
                {
                    break;
                }
            }

            if (importEntities.Any())
            {
                _logger.LogInformation("Adding entities...");
                await tenantRepository.BulkInsertRtEntitiesAsync(session, importEntities);
            }

            if (importAssociations.Any())
            {
                _logger.LogInformation("Adding associations...");
                await tenantRepository.BulkRtAssociationsAsync(session, importAssociations);
            }


            _logger.LogInformation("Add to database completed");
        }
        catch (Exception e)
        {
            CommandExecutionFailedException.BulkImportError(e);
        }
    }
}