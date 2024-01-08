using Hangfire;
using Meshmakers.Octo.Backend.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;

namespace Meshmakers.Octo.Backend.BotServices.Consumers;

internal class ModelCommandsConsumer(IBackgroundJobClient backgroundJobClient) :
    IDistributedConsumer<ImportCkCommandRequest>,
    IDistributedConsumer<ImportRtCommandRequest>,
    IDistributedConsumer<ExportRtCommandRequest>
{
    public async Task ConsumeAsync(IDistributedContext<ImportCkCommandRequest> context)
    {
        var id = backgroundJobClient.Enqueue<IImportModelJob>(job =>
            job.ImportCkAsync(context.Message.TenantId, context.Message.CacheFileKey, BotCancellationToken.Null));
        
        await context.RespondAsync(new JobCreatedResponse(id));
    }

    public async Task ConsumeAsync(IDistributedContext<ImportRtCommandRequest> context)
    {
        var id = backgroundJobClient.Enqueue<IImportModelJob>(job =>
            job.ImportRtAsync(context.Message.TenantId, context.Message.CacheFileKey, BotCancellationToken.Null));
        
        await context.RespondAsync(new JobCreatedResponse(id));
    }

    public async Task ConsumeAsync(IDistributedContext<ExportRtCommandRequest> context)
    {
        var id = backgroundJobClient.Enqueue<IExportModelJob>(job =>
            job.ExportRtAsync(context.Message.TenantId, context.Message.QueryId, BotCancellationToken.Null));
        
        await context.RespondAsync(new JobCreatedResponse(id));  
    }
}