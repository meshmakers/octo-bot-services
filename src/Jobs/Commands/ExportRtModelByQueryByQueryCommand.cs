using Meshmakers.Common.Shared;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
using Meshmakers.Octo.Runtime.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Microsoft.Extensions.Logging;
using RtEntityDto = Meshmakers.Octo.Runtime.Contracts.DataTransferObjects.RtEntityDto;

namespace Meshmakers.Octo.Backend.Jobs.Commands;

internal class ExportRtModelByQueryByQueryCommand(
    ILogger<ExportRtModelByQueryByQueryCommand> logger,
    ISystemContext systemContext,
    IRtSerializer rtSerializer)
    : CommandBase, IExportRtModelByQueryCommand
{
    public async Task ExportAsync(string tenantId, OctoObjectId queryId, string filePath,
        CancellationToken? cancellationToken)
    {
        CheckAndThrowCancellation(cancellationToken);

        var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var query = await tenantRepository.GetRtEntityByRtIdAsync(session,
                new RtEntityId(SystemCkIds.ModelId, SystemCkIds.QueryTypeId, queryId));

            CheckAndThrowCancellation(cancellationToken);

            if (query == null)
            {
                throw CommandExecutionFailedException.QueryNotFound(queryId);
            }

            var dataQueryOperation = DataQueryOperation.Create();

            var sortingDtoList = query.GetRtRecordAttributeValuesOrDefault<RtSortOrderItemRecord>("Sorting");
            if (sortingDtoList != null)
            {
                foreach (var sortDto in sortingDtoList)
                {
                    dataQueryOperation.SortOrder(sortDto.AttributePath.ToPascalCase(), (SortOrders)sortDto.SortOrder);
                }
            }

            var fieldFilterDtoList = query.GetRtRecordAttributeValuesOrDefault<RtFieldFilterRecord>("FieldFilter");
            if (fieldFilterDtoList != null)
            {
                foreach (var fieldFilterDto in fieldFilterDtoList)
                {
                    dataQueryOperation.FieldFilter(TransformAttributeName(fieldFilterDto.AttributePath),
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

            var ckTypeGraph = await tenantRepository.GetCkTypeGraphAsync(ckTypeId);

            CheckAndThrowCancellation(cancellationToken);

            var model = new RtModelRootDto();
            model.Entities.AddRange(resultSet.Items.Select(entity =>
            {
                var entityDto = new RtEntityDto
                {
                    RtId = entity.RtId,
                    RtChangedDateTime = entity.RtChangedDateTime,
                    RtCreationDateTime = entity.RtCreationDateTime,
                    RtWellKnownName = entity.RtWellKnownName,
                    CkTypeId = entity.CkTypeId ?? throw OperationFailedException.CkTypeIdUndefined()
                };

                entityDto.Attributes.AddRange(entity.Attributes.Select(pair =>
                {
                    var typeAttributeGraph = ckTypeGraph.AllAttributesByName[pair.Key];
                    return new RtAttributeDto
                    {
                        Id = typeAttributeGraph.CkAttributeId,
                        Value = pair.Value
                    };
                }));

                return entityDto;
            }));

            CheckAndThrowCancellation(cancellationToken);

            await using var streamWriter = new StreamWriter(filePath);
            await rtSerializer.SerializeAsync(streamWriter, model);

            await session.CommitTransactionAsync();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Exporting model failed");
            throw;
        }
    }

    private static string TransformAttributeName(string attributeNameDto)
    {
        var attributeName = attributeNameDto.ToPascalCase();


        return attributeName;
    }
}