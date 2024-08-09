using System.Collections.Concurrent;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Commands;

internal class ImportRtModelCommand(
    ILogger<ImportRtModelCommand> logger,
    ISystemContext systemContext,
    ICkCacheService cacheService,
    IRtYamlSerializer rtYamlSerializer,
    IRtJsonSerializer rtJsonSerializer)
    : CommandBase, IImportRtModelCommand
{
    private readonly HashSet<OctoObjectId> _entityImportIds = new();
    private readonly ConcurrentQueue<RtAssociation> _importAssociationQueue = new();

    private readonly ConcurrentQueue<RtEntity> _importEntityQueue = new();
    private readonly IRtSerializer _rtYamlSerializer = rtYamlSerializer;
    private int _associationsCount;

    public async Task ImportText(string tenantId, string jsonText, CancellationToken? cancellationToken = null)
    {
        logger.LogInformation("Importing RT entities using text started");
        
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);
        if (!cacheService.IsTenantLoaded(tenantId))
        {
            await tenantRepository.LoadCacheForTenantAsync(cacheService);
        }

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            OperationResult operationResult = new();
            var rtModelRoot = await _rtYamlSerializer.DeserializeAsync(jsonText, "-", operationResult);
            ValidateCkModels(tenantId, rtModelRoot.Dependencies);
            await ImportEntityAsync(session, rtModelRoot.Entities, tenantRepository);

            await session.CommitTransactionAsync();

            logger.LogInformation("{Count} entities, {AssociationsCount} associations imported", _entityImportIds.Count,
                _associationsCount);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Import of RT model failed");
            throw;
        }
    }

    public async Task Import(string tenantId, string filePath, string contentType, CancellationToken? cancellationToken = null)
    {
        logger.LogInformation("Importing RT entities using file started");
        
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);
        if (!cacheService.IsTenantLoaded(tenantId))
        {
            await tenantRepository.LoadCacheForTenantAsync(cacheService);
        }

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();
            await using (var stream = File.OpenRead(filePath))
            {
                if (contentType.ToLower() == MimeTypes.MimeTypeYaml)
                {
                    OperationResult operationResult = new();
                    var rtModelRootDto = await _rtYamlSerializer.DeserializeAsync(stream, filePath, operationResult);
                    ValidateCkModels(tenantId, rtModelRootDto.Dependencies);
                    await ImportEntityAsync(session, rtModelRootDto.Entities, tenantRepository);
                }
                else
                {
                    var rtDeserializeStream = await rtJsonSerializer.DeserializeStreamAsync(stream, cancellationToken);
                    rtDeserializeStream.BulkDeserialized += async (_, args) =>
                    {
                        await ImportEntityAsync(session, args.DeserializedEntities, tenantRepository);

                        args.IsHandled = true;
                    };
                    ValidateCkModels(tenantId, rtDeserializeStream.Dependencies.ToList());
                    await rtDeserializeStream.ReadAsync();
                }
            }

            await session.CommitTransactionAsync();

            logger.LogInformation("{Count} entities, {AssociationsCount} associations imported", _entityImportIds.Count,
                _associationsCount);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Import of RT model failed");
            throw;
        }
    }
    
    private void ValidateCkModels(string tenantId, ICollection<CkModelId> ckModelIds)
    {
        var missingCkModelIds = cacheService.EnsureModelIds(tenantId, ckModelIds);
        if (missingCkModelIds.Any())
        {
            throw CommandExecutionFailedException.CkModelsMissing(tenantId, missingCkModelIds); 
        }
    }

    private async Task ImportEntityAsync(IOctoSession session, IEnumerable<RtEntityDto> modelRtEntities,
        ITenantRepository tenantRepository)
    {
        await Parallel.ForEachAsync(modelRtEntities, async (modelRtEntity, token) =>
        {
            var ckTypeGraph = cacheService.GetCkType(tenantRepository.TenantId, modelRtEntity.CkTypeId);

            var rtEntity = await tenantRepository.CreateTransientRtEntityAsync(modelRtEntity.CkTypeId).ConfigureAwait(false);
            rtEntity.RtId = modelRtEntity.RtId;
            rtEntity.RtChangedDateTime = modelRtEntity.RtChangedDateTime;
            rtEntity.RtCreationDateTime = modelRtEntity.RtCreationDateTime;
            rtEntity.RtWellKnownName = modelRtEntity.RtWellKnownName;

            if (_entityImportIds.Contains(rtEntity.RtId))
            {
                logger.LogError("'{RtEntityRtId}' already imported", rtEntity.RtId);
            }

            lock (_entityImportIds)
            {
                _entityImportIds.Add(rtEntity.RtId);
            }

            token.ThrowIfCancellationRequested();

            AssignAttributes(tenantRepository, modelRtEntity, ckTypeGraph, rtEntity, "type", ckTypeGraph.CkTypeId);

            _importEntityQueue.Enqueue(rtEntity);

            if (modelRtEntity.Associations != null && modelRtEntity.Associations.Count > 0)
            {

                foreach (var association in modelRtEntity.Associations)
                {
                    var ckAssociationRoleGraph = cacheService.GetCkAssociationRole(tenantRepository.TenantId, association.RoleId);

                    var rtAssociation = new RtAssociation
                    {
                        AssociationRoleId = association.RoleId,
                        OriginRtId = rtEntity.RtId,
                        OriginCkTypeId = rtEntity.CkTypeId!,
                        TargetRtId = association.TargetRtId,
                        TargetCkTypeId = association.TargetCkTypeId,
                        TargetCkAttributeIds = association.TargetCkAttributeIds
                    };
                    
                    AssignAttributes(tenantRepository, association, ckAssociationRoleGraph, rtAssociation, "association", ckAssociationRoleGraph.CkRoleId);

                    _importAssociationQueue.Enqueue(rtAssociation);
                    Interlocked.Increment(ref _associationsCount);
                }
            }
        });

        logger.LogInformation("{EntityCount} entities (total imports of {Count}) imported", _importEntityQueue.Count,
            _entityImportIds.Count);
        await ImportToDatabase(session, tenantRepository);
    }

    private void AssignAttributes<TKey>(ITenantRepository tenantRepository, RtTypeWithAttributesDto rtTypeWithAttributesDto,
        CkTypeWithAttributesGraph ckTypeWithAttributesGraph, RtTypeWithAttributes rtTypeWithAttributes, string elementType, CkId<TKey> ckId)
        where TKey : IComparable<TKey>, ICkKey
    {
        foreach (var modelAttribute in rtTypeWithAttributesDto.Attributes)
        {
            var typeAttributeGraph =
                ckTypeWithAttributesGraph.AllAttributes.Values.FirstOrDefault(a => a.CkAttributeId.Equals(modelAttribute.Id));
            if (typeAttributeGraph == null)
            {
                logger.LogError("'{ModelAttributeId}' does not exit on type '{CkTypeId}'", modelAttribute.Id,
                    ckId);
                throw CommandExecutionFailedException.AttributeNotFound(modelAttribute.Id, elementType, ckId);
            }

            if (typeAttributeGraph.ValueType == AttributeValueTypesDto.Record)
            {
                if (modelAttribute.Value is RtRecordDto rtRecordDto)
                {
                    var ckRecordGraph = cacheService.GetCkRecord(tenantRepository.TenantId, rtRecordDto.CkRecordId);
                    if (ckRecordGraph == null)
                    {
                        logger.LogError("'{ModelAttributeId}' defines unknown record '{CkRecordId}' at type '{CkTypeId}'",
                            modelAttribute.Id,
                            rtRecordDto.CkRecordId, ckId);
                        throw CommandExecutionFailedException.RecordNotFound(rtRecordDto.CkRecordId, elementType, ckId);
                    }

                    var rtRecord = new RtRecord
                    {
                        CkRecordId = ckRecordGraph.CkRecordId
                    };
                    AssignAttributes(tenantRepository, rtRecordDto, ckRecordGraph, rtRecord, elementType, ckId);

                    rtTypeWithAttributes.SetAttributeValue(typeAttributeGraph.AttributeName, typeAttributeGraph.ValueType, rtRecord);
                }

                continue;
            }

            rtTypeWithAttributes.SetAttributeValue(typeAttributeGraph.AttributeName, typeAttributeGraph.ValueType,
                modelAttribute.Value);
        }
    }

    private async Task ImportToDatabase(IOctoSession session, ITenantRepository tenantRepository)
    {
        logger.LogInformation("Importing {Count} to database", _importEntityQueue.Count);

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
                logger.LogInformation("Adding entities...");
                await tenantRepository.BulkInsertRtEntitiesAsync(session, importEntities);
            }

            if (importAssociations.Any())
            {
                logger.LogInformation("Adding associations...");
                await tenantRepository.BulkRtAssociationsAsync(session, importAssociations);
            }


            logger.LogInformation("Add to database completed");
        }
        catch (Exception e)
        {
            throw CommandExecutionFailedException.BulkImportError(e);
        }
    }
}