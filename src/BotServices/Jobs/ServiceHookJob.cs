using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.CkModelEntities;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Newtonsoft.Json;
using NLog;
using RestSharp;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.BotServices.Jobs;

public class ServiceHookJob
{
    private const string APIKEY = "XApiKey";
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly ISystemContext _systemContext;

    public ServiceHookJob(ISystemContext systemContext)
    {
        _systemContext = systemContext;
    }

    [DisplayName("Checks for new job schedules '{0}'")]
    public async Task Run(string dataSource, IJobCancellationToken cancellationToken)
    {
        try
        {
            var startDateTime = DateTime.Now;
            var dataSourceContext = await _systemContext.CreateOrGetTenantContextAsync(dataSource);
            using var session = await dataSourceContext.Repository.StartSessionAsync();
            session.StartTransaction();

            var dataQueryQueryOperation = new DataQueryOperation
            {
                FieldFilters = new[] { new FieldFilter(Constants.EnabledAttribute, FieldFilterOperator.Equals, true) }
            };

            var serviceHookResultSet =
                await dataSourceContext.Repository.GetRtEntitiesByTypeAsync<RtSystemServiceHook>(session,
                    dataQueryQueryOperation);

            foreach (var serviceHook in serviceHookResultSet.Result)
            {
                var targetCkId = serviceHook.GetAttributeStringValueOrDefault(Constants.QueryCkIdAttribute);
                var serviceHookBaseUri =
                    serviceHook.GetAttributeStringValueOrDefault(Constants.ServiceHookBaseUriAttribute);
                var serviceHookAction =
                    serviceHook.GetAttributeStringValueOrDefault(Constants.ServiceHookActionAttribute);
                var serviceHookApiKey =
                    serviceHook.GetAttributeStringValueOrDefault(Constants.ServiceHookApiKeyAttribute);
                var dataQueryOperation = new DataQueryOperation
                {
                    FieldFilters = JsonConvert
                        .DeserializeObject<FieldFilterDto[]>(
                            serviceHook.GetAttributeStringValueOrDefault(Constants.FieldFilterAttribute))
                        .Select(f =>
                            new FieldFilter(TransformAttributeName(f.AttributeName), (FieldFilterOperator)f.Operator,
                                f.ComparisonValue))
                };

                if (CheckCancellation(cancellationToken.ShutdownToken))
                {
                    return;
                }

                var result =
                    await dataSourceContext.Repository.GetRtEntitiesByTypeAsync(session, targetCkId, dataQueryOperation,
                        0, 500);

                Logger.Info(
                    $"Processing '{result.TotalCount}' entities of type '{targetCkId}' at '{startDateTime}");

                try
                {
                    await CallServiceHook(serviceHookBaseUri, serviceHookAction, serviceHookApiKey, result.Result,
                        cancellationToken.ShutdownToken);
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

    private async Task CallServiceHook(string baseUri, string webServiceAction, string apiKey,
        IEnumerable<RtEntity> entities,
        CancellationToken cancellationToken)
    {
        var result = entities.Select(x => x.RtId.ToString());

        var client = new RestClient(baseUri);
        var request = new RestRequest(webServiceAction, Method.Post);
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.AddHeader(APIKEY, apiKey);
        }

        request.AddJsonBody(result);

        var response = await client.ExecutePostAsync(request, cancellationToken);
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
