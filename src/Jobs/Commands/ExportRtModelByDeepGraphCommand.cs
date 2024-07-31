using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Commands;

internal class ExportRtModelByDeepGraphCommand(
    ILogger<ExportRtModelByDeepGraphCommand> logger,
    ISystemContext systemContext,
    IRtSerializer rtSerializer) : CommandBase, IExportRtModelByDeepGraphCommand
{
    public async Task ExportAsync(string tenantId, IEnumerable<OctoObjectId> originRtIds, CkId<CkTypeId> originCkTypeId,
        string filePath,
        CancellationToken? cancellationToken)
    {
        CheckAndThrowCancellation(cancellationToken);

        var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);
        var session = await tenantRepository.GetSessionAsync();
        try
        {
            var dataQueryOperation = DataQueryOperation.Create();
            var r = await tenantRepository.GetRtDeepGraphAsync(session, originRtIds, originCkTypeId,
                dataQueryOperation);

            CheckAndThrowCancellation(cancellationToken);

            var groupedByCkType = r.Items.GroupBy(x => x.Id.CkTypeId);
            var model = new RtModelRootDto();
            foreach (var grouping in groupedByCkType)
            {
                var s = await tenantRepository.GetRtEntitiesByIdAsync(session, grouping.Key, grouping.Select(x => x.Id.RtId).ToList(), dataQueryOperation);
                
                CheckAndThrowCancellation(cancellationToken);

                var ckTypeGraph = await tenantRepository.GetCkTypeGraphAsync(grouping.Key);

                foreach (var rtEntity in s.Items)
                {
                    var entityDto = new RtEntityDto
                    {
                        RtId = rtEntity.RtId,
                        RtChangedDateTime = rtEntity.RtChangedDateTime,
                        RtCreationDateTime = rtEntity.RtCreationDateTime,
                        RtWellKnownName = rtEntity.RtWellKnownName,
                        CkTypeId = rtEntity.CkTypeId ?? throw OperationFailedException.CkTypeIdUndefined()
                    };

                    entityDto.Attributes.AddRange(rtEntity.Attributes.Select(pair =>
                    {
                        var attributeCacheItem = ckTypeGraph.AllAttributes[pair.Key];
                        return new RtAttributeDto
                        {
                            Id = attributeCacheItem.CkAttributeId,
                            Value = pair.Value
                        };
                    }));
                    
                    model.Entities.Add(entityDto);
                }
            }

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
}