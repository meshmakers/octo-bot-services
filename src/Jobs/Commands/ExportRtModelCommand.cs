using Meshmakers.Common.Shared;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.ConstructionKit.Generated.System.v1;
using Meshmakers.Octo.Runtime.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Microsoft.Extensions.Logging;
using RtEntityDto = Meshmakers.Octo.Runtime.Contracts.DataTransferObjects.RtEntityDto;

namespace Meshmakers.Octo.Backend.Jobs.Commands;

internal class ExportRtModelCommand : IExportRtModelCommand
{
    private readonly ILogger<ExportRtModelCommand> _logger;
    private readonly IRtSerializer _rtSerializer;
    private readonly ISystemContext _systemContext;

    internal ExportRtModelCommand(ILogger<ExportRtModelCommand> logger, ISystemContext systemContext, IRtSerializer rtSerializer)
    {
        _logger = logger;
        _systemContext = systemContext;
        _rtSerializer = rtSerializer;
    }


    public async Task ExportAsync(string tenantId, OctoObjectId queryId, string filePath,
        CancellationToken? cancellationToken)
    {
        ITenantContext tenantContext = _systemContext;
        if (tenantId != _systemContext.TenantId)
        {
            tenantContext = await _systemContext.GetChildTenantContextAsync(tenantId);
        }   
        
        var tenantRepository = tenantContext.GetTenantRepository();

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var query = await tenantRepository.GetRtEntityByRtIdAsync(session,
                new RtEntityId(SystemCkIds.ModelId, SystemCkIds.QueryTypeId, queryId));

            if (CheckCancellation(cancellationToken))
            {
                throw new OperationCanceledException();
            }

            if (query == null)
            {
                throw CommandExecutionFailedException.QueryNotFound(queryId);
            }

            var dataQueryOperation = DataQueryOperation.Create();

            var sortingDtoList = query.GetAttributeStringValueOrDefault("Sorting")?.Deserialize<ICollection<SortDto>>();
            if (sortingDtoList != null)
            {
                foreach (var sortDto in sortingDtoList)
                {
                    dataQueryOperation.SortOrder(sortDto.AttributeName.ToPascalCase(), (SortOrders)sortDto.SortOrder);
                }
            }

            var fieldFilterDtoList =
                query.GetAttributeStringValueOrDefault("FieldFilter")?.Deserialize<ICollection<FieldFilterDto>>();
            if (fieldFilterDtoList != null)
            {
                foreach (var fieldFilterDto in fieldFilterDtoList)
                {
                    dataQueryOperation.FieldFilter(TransformAttributeName(fieldFilterDto.AttributeName),
                        (FieldFilterOperator)fieldFilterDto.Operator, fieldFilterDto.ComparisonValue);
                }
            }

            var ckTypeIdString = query.GetAttributeStringValueOrDefault("QueryCkTypeId");
            if (string.IsNullOrWhiteSpace(ckTypeIdString))
            {
                throw CommandExecutionFailedException.QueryCkTypeIdNotSet(queryId);
            }

            var ckTypeId = new CkId<CkTypeId>(ckTypeIdString);

            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync(session, ckTypeId, dataQueryOperation);

            var entityCacheItem = await tenantRepository.GetCkTypeGraphAsync(ckTypeId);

            var model = new RtModelRootDto();
            model.Entities.AddRange(resultSet.Items.Select(entity =>
            {
                var exEntity = new RtEntityDto
                {
                    RtId = entity.RtId,
                    RtChangedDateTime = entity.RtChangedDateTime,
                    RtCreationDateTime = entity.RtCreationDateTime,
                    RtWellKnownName = entity.RtWellKnownName,
                    CkTypeId = entity.CkTypeId ?? throw OperationFailedException.CkTypeIdUndefined()
                };

                exEntity.Attributes.AddRange(entity.Attributes.Select(pair =>
                {
                    var attributeCacheItem = entityCacheItem.AllAttributes[pair.Key];
                    return new RtAttributeDto
                    {
                        Id = attributeCacheItem.CkAttributeId,
                        Value = pair.Value
                    };
                }));

                return exEntity;
            }));

            await using var streamWriter = new StreamWriter(filePath);
            await _rtSerializer.SerializeAsync(streamWriter, model);

            await session.CommitTransactionAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Exporting model failed");
            throw;
        }
    }

    private static string TransformAttributeName(string attributeNameDto)
    {
        var attributeName = attributeNameDto.ToPascalCase();


        return attributeName;
    }

    private static bool CheckCancellation(CancellationToken? cancellationToken)
    {
        if (cancellationToken != null && cancellationToken.Value.IsCancellationRequested)
        {
            return true;
        }

        return false;
    }
}