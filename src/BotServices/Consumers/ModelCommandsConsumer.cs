using Hangfire;
using Meshmakers.Octo.Backend.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

namespace Meshmakers.Octo.Backend.BotServices.Consumers;

// ReSharper disable once ClassNeverInstantiated.Global
internal class ModelCommandsConsumer(IBackgroundJobClient backgroundJobClient) :
    IDistributedConsumer<ImportCkCommandRequest>,
    IDistributedConsumer<ImportCkBatchCommandRequest>,
    IDistributedConsumer<ImportRtCommandRequest>,
    IDistributedConsumer<ExportRtByQueryCommandRequest>,
    IDistributedConsumer<ExportRtByDeepGraphCommandRequest>
{
    public async Task ConsumeAsync(IDistributedContext<ImportCkCommandRequest> context)
    {
        var id = backgroundJobClient.Enqueue<IImportModelJob>(job =>
            job.ImportCkAsync(context.Message.TenantId, context.Message.CacheFileKey, BotCancellationToken.Null));
        
        await context.RespondAsync(new JobCreatedResponse(id));
    }

    public async Task ConsumeAsync(IDistributedContext<ImportCkBatchCommandRequest> context)
    {
        var id = backgroundJobClient.Enqueue<IImportModelJob>(job =>
            job.ImportCkBatchAsync(context.Message.TenantId, context.Message.CacheFileKeys, BotCancellationToken.Null));

        await context.RespondAsync(new JobCreatedResponse(id));
    }

    public async Task ConsumeAsync(IDistributedContext<ImportRtCommandRequest> context)
    {
        var id = backgroundJobClient.Enqueue<IImportModelJob>(job =>
            job.ImportRtAsync(context.Message.TenantId, context.Message.ImportStrategy, context.Message.CacheFileKey,
                context.Message.InitiatedBySubjectId, BotCancellationToken.Null));
        
        await context.RespondAsync(new JobCreatedResponse(id));
    }

    public async Task ConsumeAsync(IDistributedContext<ExportRtByQueryCommandRequest> context)
    {
        RequireCallerContext(context.Message.TenantId, "query", context.Message.InitiatedBySubjectId);

        var id = backgroundJobClient.Enqueue<IExportModelJob>(job =>
            job.ExportRtModelByQueryAsync(context.Message, BotCancellationToken.Null));
        
        await context.RespondAsync(new JobCreatedResponse(id));  
    }

    public async Task ConsumeAsync(IDistributedContext<ExportRtByDeepGraphCommandRequest> context)
    {
        RequireCallerContext(context.Message.TenantId, "deep graph", context.Message.InitiatedBySubjectId);

        var id = backgroundJobClient.Enqueue<IExportModelJob>(job =>
            job.ExportRtModelByDeepGraphAsync(context.Message, BotCancellationToken.Null));
        
        await context.RespondAsync(new JobCreatedResponse(id));  
    }

    /// <summary>
    ///     AB#6734: an export runs with the data permissions of the user who requested it. A request without caller
    ///     identity (an older asset repository build) is refused here - no job is enqueued and no file is produced -
    ///     instead of being exported unfiltered as the system.
    /// </summary>
    private static void RequireCallerContext(string tenantId, string exportKind, string? initiatedBySubjectId)
    {
        if (string.IsNullOrWhiteSpace(initiatedBySubjectId))
        {
            throw new InvalidOperationException(
                $"The {exportKind} export for tenant '{tenantId}' carries no caller identity and is refused. " +
                "An export runs with the data permissions of the requesting user; update the asset repository " +
                "and request the export again.");
        }
    }
}
