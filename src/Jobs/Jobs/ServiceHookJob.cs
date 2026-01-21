using Meshmakers.Common.Shared;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Bot.Generated.System.Bot.v2;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Newtonsoft.Json;
using NLog;
using RestSharp;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

public class ServiceHookJob(ISystemContext systemContext) : IServiceHookJob
{
    private const string Apikey = "XApiKey";
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public async Task Run(string tenantId, IBotCancellationToken? cancellationToken)
    {
        try
        {
            if (!await systemContext.IsSystemTenantExistingAsync())
            {
                return;
            }
            
            var startDateTime = DateTime.Now;
            var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);

            using var session = await tenantRepository.GetSessionAsync();
            session.StartTransaction();

            var queryOptions = RtEntityQueryOptions.Create()
                .FieldFilter(SystemCkIds.EnabledAttribute, FieldFilterOperator.Equals, true);

            var serviceHookResultSet =
                await tenantRepository.GetRtEntitiesByTypeAsync<RtServiceHook>(session,
                    queryOptions);

            foreach (var serviceHook in serviceHookResultSet.Items)
            {
                var targetCkId = serviceHook.GetAttributeStringValueOrDefault(SystemCkIds.QueryCkTypeIdAttribute);
                var serviceHookBaseUri =
                    serviceHook.GetAttributeStringValueOrDefault(SystemBotCkIds.ServiceHookUriAttribute);
                var serviceHookAction =
                    serviceHook.GetAttributeStringValueOrDefault(SystemBotCkIds.ServiceHookActionAttribute);
                var serviceHookApiKey =
                    serviceHook.GetAttributeStringValueOrDefault(SystemBotCkIds.ServiceHookApiKeyAttribute);
                var fieldFilter = serviceHook.GetAttributeStringValueOrDefault(SystemCkIds.QueryFieldFilterAttribute);

                if (!string.IsNullOrWhiteSpace(fieldFilter) || string.IsNullOrWhiteSpace(targetCkId) ||
                    string.IsNullOrWhiteSpace(fieldFilter) || string.IsNullOrWhiteSpace(serviceHookBaseUri) ||
                    string.IsNullOrWhiteSpace(serviceHookAction))
                {
                    continue;
                }

                var entityQueryOptions = RtEntityQueryOptions.Create();

                var fieldFilters = JsonConvert.DeserializeObject<FieldFilterDto[]>(fieldFilter);
                if (fieldFilters == null)
                {
                    continue;
                }

                foreach (var f in fieldFilters)
                {
                    entityQueryOptions = entityQueryOptions.FieldFilter(TransformAttributeName(f.AttributePath),
                        (FieldFilterOperator)f.Operator,
                        f.ComparisonValue);
                }

                if (CheckCancellation(cancellationToken?.ShutdownToken))
                {
                    return;
                }

                var result =
                    await tenantRepository.GetRtEntitiesByTypeAsync(session, targetCkId, entityQueryOptions,
                        0, 500);

                Logger.Info(
                    $"Processing '{result.TotalCount}' entities of type '{targetCkId}' at '{startDateTime}");

                try
                {
                    await CallServiceHook(serviceHookBaseUri, serviceHookAction, serviceHookApiKey, result.Items,
                        cancellationToken?.ShutdownToken);
                }
                catch (Exception e)
                {
                    Logger.Error(e);
                    // Ignore the error because the job is recurring
                }

                Logger.Info($"Processing done (start was at '{startDateTime}')");
            }

            await session.CommitTransactionAsync();
        }
        catch (Exception e)
        {
            Logger.Error(e);
            throw;
        }
    }

    private static string TransformAttributeName(string attributeNameDto)
    {
        var attributeName = attributeNameDto.ToPascalCase();

        return attributeName;
    }

    private async Task CallServiceHook(string baseUri, string webServiceAction, string? apiKey,
        IEnumerable<RtEntity> entities,
        CancellationToken? cancellationToken)
    {
        var result = entities.Select(x => x.RtId.ToString());

        var client = new RestClient(baseUri);
        var request = new RestRequest(webServiceAction, Method.Post);
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.AddHeader(Apikey, apiKey);
        }

        request.AddJsonBody(result);

        var response = await client.ExecutePostAsync(request, cancellationToken!.Value);
        ValidateResponse(response);
    }

    private static bool CheckCancellation(CancellationToken? cancellationToken)
    {
        if (cancellationToken != null && cancellationToken.Value.IsCancellationRequested)
        {
            return true;
        }

        return false;
    }

    private static void ValidateResponse(RestResponse response)
    {
        if (!response.IsSuccessful)
        {
            if (!string.IsNullOrEmpty(response.ErrorMessage))
            {
                throw new ServiceHookException(response.ErrorMessage, response.ErrorException);
            }

            throw new ServiceHookResultException(response.Content, response.StatusCode);
        }
    }
}