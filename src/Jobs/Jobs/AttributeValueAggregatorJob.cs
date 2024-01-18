using System.ComponentModel;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;
using NLog;
using SystemBotCkModel.ConstructionKit.Generated.System.Bot.v1;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
///     HangFire job to aggregate attribute values for auto complete
/// </summary>
public class AttributeValueAggregatorJob : IAttributeValueAggregatorJob
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly ICkCacheService _ckCacheService;
    private readonly IDistributionEventHubService _distributionEventHubService;
    private readonly ISystemContext _systemContext;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="systemContext">System context object</param>
    /// <param name="ckCacheService"></param>
    /// <param name="distributionEventHubService">Distribution event hub service</param>
    public AttributeValueAggregatorJob(ISystemContext systemContext, ICkCacheService ckCacheService,
        IDistributionEventHubService distributionEventHubService)
    {
        _systemContext = systemContext;
        _ckCacheService = ckCacheService;
        _distributionEventHubService = distributionEventHubService;
    }

    /// <summary>
    ///     Aggregates all aggregatable attributes
    /// </summary>
    /// <param name="tenantId">The corresponding data source</param>
    /// <param name="cancellationToken">An cancellation token to abort the job</param>
    /// <returns></returns>
    [DisplayName("Aggregates all attributes of data source '{0}'")]
    public async Task Run(string tenantId, IBotCancellationToken? cancellationToken)
    {
        try
        {
            Logger.Info($"Reading aggregatable attributes '{tenantId}'");

            if (!await _systemContext.IsSystemTenantExistingAsync())
            {
                return;
            }
            var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

            using var session = await tenantRepository.GetSessionAsync();
            session.StartTransaction();

            var dataOperation = DataQueryOperation.Create();
            var configurationResult =
                await tenantRepository.GetRtEntitiesByTypeAsync<RtAttributeAggregateConfiguration>(session, dataOperation);

            if (!configurationResult.Items.Any())
            {
                Logger.Info($"No aggregatable attributes found for data source '{tenantId}'");
            }

            // TODO: GetRtAssociationTargetsAsync needs to be optimized. We need to have better descriptions (what parameter is what) and we need to have a way the get attributes of assocs
            // var originEntities = configurationResult.Items.Select(x => x.RtId);
            // var assocResult = await tenantRepository
            //     .GetRtAssociationTargetsAsync<RtAttributeAggregateConfiguration, RtEntity>(session, originEntities,
            //         SystemBotCkIds.Configures, GraphDirections.Outbound, null, dataOperation);

            await _distributionEventHubService.PublishAsync(new PreUpdateTenant(tenantId));

            foreach (var configurationRtEntity in configurationResult.Items)
            {
                cancellationToken?.ThrowIfCancellationRequested();

                var rtAssociations = await tenantRepository.GetRtAssociationsAsync(session, configurationRtEntity.RtId,
                    GraphDirections.Outbound, SystemBotCkIds.Configures);
                var rtAssociation = rtAssociations.FirstOrDefault();
                if (rtAssociation == null)
                {
                    continue;
                }

                if (!configurationRtEntity.IsAutoCompleteEnabled)
                {
                    continue;
                }

                CkId<CkAttributeId>? attributeId = null;
                if (!rtAssociation.Attributes.TryGetValue(SystemBotCkIds.SelectedAttributeIdAttribute, out var attributeValue))
                {
                    continue;
                }

                if (attributeValue is string s1)
                {
                    attributeId = s1;
                }

                cancellationToken?.ThrowIfCancellationRequested();

                var ckTypeGraph = _ckCacheService.GetCkType(tenantId, rtAssociation.TargetCkTypeId);
                if (attributeId == null || !ckTypeGraph.AllAttributes.TryGetValue(attributeId.Value, out var attributeCacheItem))
                {
                    continue;
                }

                var autoCompleteTexts = await tenantRepository.ExtractAutoCompleteValuesAsync(session,
                    rtAssociation.TargetCkTypeId,
                    attributeCacheItem.AttributeName, configurationRtEntity.AutoCompleteFilter,
                    configurationRtEntity.AutoCompleteLimit);

                cancellationToken?.ThrowIfCancellationRequested();

                await tenantRepository.UpdateAutoCompleteTexts(session, ckTypeGraph.CkTypeId,
                    attributeCacheItem.AttributeName, autoCompleteTexts.Select(x => x.Text));
            }

            await session.CommitTransactionAsync();

            await _distributionEventHubService.PublishAsync(new PosUpdateTenant(tenantId));

            Logger.Info($"Aggregation of attribute values of data source '{tenantId}' completed.");
        }
        catch (Exception e)
        {
            Logger.Error(e, "Aggregation failed with error.");
            throw;
        }
    }
}