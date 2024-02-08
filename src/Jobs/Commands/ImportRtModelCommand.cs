using System.Collections.Concurrent;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
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
    private readonly IRtSerializer _rtYamlSerializer;
    private readonly IRtJsonSerializer _rtJsonSerializer;
    private readonly ISystemContext _systemContext;
    private readonly ICkCacheService _cacheService;
    private int _associationsCount;

    public ImportRtModelCommand(ILogger<ImportRtModelCommand> logger, ISystemContext systemContext,
        ICkCacheService cacheService,
        IRtYamlSerializer rtYamlSerializer, IRtJsonSerializer rtJsonSerializer)
    {
        _logger = logger;
        _systemContext = systemContext;
        _cacheService = cacheService;
        _rtYamlSerializer = rtYamlSerializer;
        _rtJsonSerializer = rtJsonSerializer;

        _entityImportIds = new HashSet<OctoObjectId>();
        _importEntityQueue = new ConcurrentQueue<RtEntity>();
        _importAssociationQueue = new ConcurrentQueue<RtAssociation>();
    }

    public async Task ImportText(string tenantId, string jsonText, CancellationToken? cancellationToken = null)
    {
        _logger.LogInformation("Importing RT entities using text started");

        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            OperationResult operationResult = new();
            var rtModelRoot = await _rtYamlSerializer.DeserializeAsync(jsonText, "-", operationResult);
            await ImportEntityAsync(session, rtModelRoot.Entities, tenantRepository);

            await session.CommitTransactionAsync();

            _logger.LogInformation("{Count} entities, {AssociationsCount} associations imported", _entityImportIds.Count,
                _associationsCount);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Import of RT model failed");
            throw;
        }
    }

    public async Task Import(string tenantId, string filePath, string contentType, CancellationToken? cancellationToken = null)
    {
        _logger.LogInformation("Importing RT entities using file started");

        var session = await _systemContext.GetSystemSessionAsync();
        try
        {
            var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

            session.StartTransaction();
            await using (var stream = File.OpenRead(filePath))
            {
                if (contentType.ToLower() == "text/yaml")
                {
                    OperationResult operationResult = new();
                    var rtModelRootDto = await _rtYamlSerializer.DeserializeAsync(stream, filePath, operationResult);
                    await ImportEntityAsync(session, rtModelRootDto.Entities, tenantRepository);
                }
                else
                {
                    var rtDeserializeStream = await _rtJsonSerializer.DeserializeStreamAsync(stream, cancellationToken);
                    rtDeserializeStream.BulkDeserialized += async (_, args) =>
                    {
                        await ImportEntityAsync(session, args.DeserializedEntities, tenantRepository);

                        args.IsHandled = true;
                    };
                    await rtDeserializeStream.ReadAsync();
                }
            }

            await session.CommitTransactionAsync();

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
            var ckTypeGraph = await tenantRepository.GetCkTypeGraphAsync(modelRtEntity.CkTypeId);

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

            AssignAttributes(tenantRepository, modelRtEntity, ckTypeGraph, rtEntity, ckTypeGraph.CkTypeId);

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

    private void AssignAttributes(ITenantRepository tenantRepository, RtTypeWithAttributesDto rtTypeWithAttributesDto,
        CkTypeWithAttributesGraph ckTypeWithAttributesGraph, RtTypeWithAttributes rtTypeWithAttributes, CkId<CkTypeId> ckTypeId)
    {
        foreach (var modelAttribute in rtTypeWithAttributesDto.Attributes)
        {
            var typeAttributeGraph =
                ckTypeWithAttributesGraph.AllAttributes.Values.FirstOrDefault(a => a.CkAttributeId.Equals(modelAttribute.Id));
            if (typeAttributeGraph == null)
            {
                _logger.LogError("'{ModelAttributeId}' does not exit on type '{CkTypeId}'", modelAttribute.Id,
                    ckTypeId);
                throw CommandExecutionFailedException.AttributeNotFound(modelAttribute.Id,
                    ckTypeId);
            }

            if (typeAttributeGraph.ValueType == AttributeValueTypesDto.Record)
            {
                if (modelAttribute.Value is RtRecordDto rtRecordDto)
                {
                    var ckRecordGraph = _cacheService.GetCkRecord(tenantRepository.TenantId, rtRecordDto.CkRecordId);
                    if (ckRecordGraph == null)
                    {
                        _logger.LogError("'{ModelAttributeId}' defines unknown record '{CkRecordId}' at type '{CkTypeId}'",
                            modelAttribute.Id,
                            rtRecordDto.CkRecordId, ckTypeId);
                        throw CommandExecutionFailedException.RecordNotFound(
                            rtRecordDto.CkRecordId, ckTypeId);
                    }

                    var rtRecord = new RtRecord
                    {
                        CkRecordId = ckRecordGraph.CkRecordId
                    };
                    AssignAttributes(tenantRepository, rtRecordDto, ckRecordGraph, rtRecord, ckTypeId);

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
            throw CommandExecutionFailedException.BulkImportError(e);
        }
    }
}