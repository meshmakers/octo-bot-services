using Meshmakers.Common.Shared;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Runtime.Contracts.TransportContainer;
using Meshmakers.Octo.Runtime.Contracts.TransportContainer.DTOs;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Commands;

internal class ExportRtModelByQueryByQueryCommand(
    ILogger<ExportRtModelByQueryByQueryCommand> logger,
    ISystemContext systemContext,
    ICkCacheService ckCacheService,
    IRtEntityToTcDtoConverter rtEntityToDtoConverter,
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
                new RtEntityId(SystemCkIds.RtCkPersistentQueryTypeId, queryId));

            CheckAndThrowCancellation(cancellationToken);

            if (query == null)
            {
                throw CommandExecutionFailedException.QueryNotFound(queryId);
            }

            var queryOptions = RtEntityQueryOptions.Create();

            var sortingDtoList = query.GetRtRecordAttributeValuesOrDefault<RtSortOrderItemRecord>("Sorting");
            if (sortingDtoList != null)
            {
                foreach (var sortDto in sortingDtoList)
                {
                    queryOptions.SortOrder(sortDto.AttributePath.ToPascalCase(), (SortOrders)sortDto.SortOrder);
                }
            }

            var fieldFilterDtoList = query.GetRtRecordAttributeValuesOrDefault<RtFieldFilterRecord>("FieldFilter");
            if (fieldFilterDtoList != null)
            {
                foreach (var fieldFilterDto in fieldFilterDtoList)
                {
                    queryOptions.FieldFilter(TransformAttributeName(fieldFilterDto.AttributePath),
                        FieldFilterOperatorDtoExtensions.FromCkModelEnum(fieldFilterDto.Operator),
                        fieldFilterDto.ComparisonValue);
                }
            }

            var ckTypeIdString = query.GetAttributeStringValueOrDefault("QueryCkTypeId");
            if (string.IsNullOrWhiteSpace(ckTypeIdString))
            {
                throw CommandExecutionFailedException.QueryCkTypeIdNotSet(queryId);
            }

            var ckTypeId = new RtCkId<CkTypeId>(ckTypeIdString);

            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync(session, ckTypeId, queryOptions);

            // Ensure the cache is loaded for the tenant
            await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

            CheckAndThrowCancellation(cancellationToken);

            var model = new RtModelRootTcDto();
            model.Entities.AddRange(resultSet.Items.Select(entity => rtEntityToDtoConverter.Convert(tenantId, entity)));

            CheckAndThrowCancellation(cancellationToken);

            // Automatically determine required CK model dependencies from exported entities
            model.Dependencies.AddRange(
                DetermineModelDependencies(tenantId, model.Entities));

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

    private List<CkModelIdVersionRange> DetermineModelDependencies(string tenantId,
        IEnumerable<RtEntityTcDto> entities)
    {
        var requiredModelIds = new HashSet<CkModelId>();
        foreach (var entity in entities)
        {
            var ckTypeGraph = ckCacheService.GetRtCkType(tenantId, entity.CkTypeId);
            requiredModelIds.Add(ckTypeGraph.CkTypeId.ModelId);
        }

        // Convert to version ranges using [major.minor,major+1.0) pattern
        return requiredModelIds
            .Where(m => m.Name != "System") // System model is always available
            .Select(m => new CkModelIdVersionRange(m.Name,
                $"[{m.Version.Major}.{m.Version.Minor},{m.Version.Major + 1}.0)"))
            .ToList();
    }

    private static string TransformAttributeName(string attributeNameDto)
    {
        var attributeName = attributeNameDto.ToPascalCase();


        return attributeName;
    }
}