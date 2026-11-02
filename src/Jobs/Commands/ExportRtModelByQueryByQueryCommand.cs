using Meshmakers.Common.Shared;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
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
    public async Task ExportAsync(string tenantId, OctoObjectId queryId, RtSecurityContext securityContext,
        string filePath, CancellationToken? cancellationToken)
    {
        CheckAndThrowCancellation(cancellationToken);

        // AB#6734: the session carries the requesting caller, never the system context (which bypasses every
        // data permission); the engine then filters the rows the query returns.
        if (!ExportCallerContext.IsUsable(securityContext))
        {
            throw JobFailedException.ExportCallerContextMissing(tenantId, "query");
        }

        var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);

        // AB#8080: disposing the session aborts a transaction that was not committed (refused stored query, missing
        // query, repository or file error) and returns the server session. Without it a failed export kept its
        // transaction open until MongoDB expired it (transactionLifetimeLimitSeconds, default 60 s), and a tenant
        // reset or index build on that database waited for it.
        using var session = await ExportCallerContext.OpenSessionAsync(tenantRepository, securityContext, tenantId, "query");
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

            var ckTypeIdString = query.GetAttributeStringValueOrDefault("QueryCkTypeId");
            if (string.IsNullOrWhiteSpace(ckTypeIdString))
            {
                throw CommandExecutionFailedException.QueryCkTypeIdNotSet(queryId);
            }

            var ckTypeId = new RtCkId<CkTypeId>(ckTypeIdString);

            var sortingDtoList = query.GetRtRecordAttributeValuesOrDefault<RtSortOrderItemRecord>("Sorting");
            var fieldFilterDtoList = query.GetRtRecordAttributeValuesOrDefault<RtFieldFilterRecord>("FieldFilter");

            // AB#8049: a stored filter or sort order on a Hidden attribute is an oracle for its value (the set of exported
            // rows answers the guess), so it is refused before the query runs. The cache must be loaded for the check.
            await tenantRepository.LoadCacheForTenantAsync(ckCacheService);
            StoredQueryAccessGuard.EnsureNotHidden(ckCacheService, tenantId, queryId, ckTypeId,
                (fieldFilterDtoList?.Select(f => ((string?)f.AttributePath, StoredQueryAccessGuard.FieldFilterUsage)) ?? [])
                .Concat(sortingDtoList?.Select(s => ((string?)s.AttributePath, StoredQueryAccessGuard.SortOrderUsage)) ??
                        []));

            var queryOptions = RtEntityQueryOptions.Create();

            if (sortingDtoList != null)
            {
                foreach (var sortDto in sortingDtoList)
                {
                    queryOptions.SortOrder(sortDto.AttributePath.ToPascalCase(), (SortOrders)sortDto.SortOrder);
                }
            }

            if (fieldFilterDtoList != null)
            {
                foreach (var fieldFilterDto in fieldFilterDtoList)
                {
                    queryOptions.FieldFilter(TransformAttributeName(fieldFilterDto.AttributePath),
                        FieldFilterOperatorDtoExtensions.FromCkModelEnum(fieldFilterDto.Operator),
                        fieldFilterDto.ComparisonValue);
                }
            }

            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync(session, ckTypeId, queryOptions);

            CheckAndThrowCancellation(cancellationToken);

            var model = new RtModelRootTcDto();
            // AB#7471: a query without an explicit sorting returns database order, which is not stable; fall back to RtId
            // so two exports of the same data are byte-identical. A sorting defined on the query is kept as is.
            var items = sortingDtoList is { Count: > 0 }
                ? resultSet.Items
                : resultSet.Items.OrderBy(e => e.RtId.ToString(), StringComparer.Ordinal);
            model.Entities.AddRange(items.Select(entity => rtEntityToDtoConverter.Convert(tenantId, entity)));

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
            .OrderBy(m => m.Name, StringComparer.Ordinal)
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