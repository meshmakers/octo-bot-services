using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Runtime.Contracts.TransportContainer;
using Meshmakers.Octo.Runtime.Contracts.TransportContainer.DTOs;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Commands;

internal class ExportRtModelByDeepGraphCommand(
    ILogger<ExportRtModelByDeepGraphCommand> logger,
    ISystemContext systemContext,
    ICkCacheService ckCacheService,
    IRtEntityToTcDtoConverter rtEntityToDtoConverter,
    IRtSerializer rtSerializer) : CommandBase, IExportRtModelByDeepGraphCommand
{
    public async Task ExportAsync(string tenantId, ExportRtByDeepGraphCommandRequest rtByDeepGraphCommandRequest,
        string filePath,
        CancellationToken? cancellationToken)
    {
        CheckAndThrowCancellation(cancellationToken);

        var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);

            // Ensure the cache is loaded for the tenant
        await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var queryOptions = RtEntityQueryOptions.Create();
            var resultSet = await tenantRepository.GetRtDeepGraphAsync(session,
                rtByDeepGraphCommandRequest.OriginRtIds, rtByDeepGraphCommandRequest.OriginCkTypeId,
                queryOptions);

            CheckAndThrowCancellation(cancellationToken);

            var itemsDictionary = resultSet.Items.ToDictionary(k => k.Id.RtId, v => v);
            var groupedByCkType = resultSet.Items.GroupBy(x => x.Id.CkTypeId);
            var model = new RtModelRootTcDto();
            foreach (var grouping in groupedByCkType)
            {
                var s = await tenantRepository.GetRtEntitiesByIdAsync(session, grouping.Key,
                    grouping.Select(x => x.Id.RtId).ToList(), queryOptions);

                CheckAndThrowCancellation(cancellationToken);

                // Ensure the cache is loaded for the tenant
                await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

                foreach (var rtEntity in s.Items)
                {
                    var entityDto = rtEntityToDtoConverter.Convert(tenantId, rtEntity);

                    if (itemsDictionary.TryGetValue(rtEntity.RtId, out var item))
                    {
                        entityDto.Associations = new List<RtAssociationTcDto>();

                        foreach (var associationItem in item.Associations)
                        {
                            var roleId = associationItem.AssociationRoleId ??
                                         throw OperationFailedException.AssociationRoleIdUndefined();
                            var ckAssociationRoleGraph = ckCacheService.GetRtCkAssociationRole(tenantId, roleId);

                            var associationDto = new RtAssociationTcDto
                            {
                                RoleId = roleId,
                                TargetRtId = associationItem.TargetRtId,
                                TargetCkTypeId = associationItem.TargetCkTypeId ??
                                                 throw OperationFailedException.CkTypeIdUndefined(),
                                TargetCkAttributeIds = associationItem.TargetCkAttributeIds
                            };

                            associationDto.Attributes.AddRange(associationItem.Attributes.Select(pair =>
                            {
                                var typeAttributeGraph = ckAssociationRoleGraph.AllAttributesByName[pair.Key];
                                return new RtAttributeTcDto
                                {
                                    Id = typeAttributeGraph.CkAttributeId.ToRtCkId(),
                                    Value = pair.Value
                                };
                            }));

                            entityDto.Associations.Add(associationDto);
                        }
                    }

                    model.Entities.Add(entityDto);
                }
            }

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

        // Follow transitive dependencies
        var allRequired = new HashSet<CkModelId>();
        var loadedModelIds = ckCacheService.GetCkModelIds(tenantId);
        var dependencyLookup = loadedModelIds
            .ToDictionary(m => m, _ => (ICollection<CkModelId>)[]);

        // Build lookup from the cache - we get all model IDs and their dependencies
        var queue = new Queue<CkModelId>(requiredModelIds);
        while (queue.Count > 0)
        {
            var modelId = queue.Dequeue();
            if (!allRequired.Add(modelId))
            {
                continue;
            }
        }

        // Convert to version ranges using [major.minor,major+1.0) pattern
        return allRequired
            .Where(m => m.Name != "System") // System model is always available
            .Select(m => new CkModelIdVersionRange(m.Name,
                $"[{m.Version.Major}.{m.Version.Minor},{m.Version.Major + 1}.0)"))
            .ToList();
    }
}