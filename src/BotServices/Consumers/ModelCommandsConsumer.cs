using Hangfire;
using Meshmakers.Octo.Backend.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

namespace Meshmakers.Octo.Backend.BotServices.Consumers;

// ReSharper disable once ClassNeverInstantiated.Global
internal class ModelCommandsConsumer(IBackgroundJobClient backgroundJobClient) :
    IDistributedConsumer<ImportCkCommandRequest>,
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

    public async Task ConsumeAsync(IDistributedContext<ImportRtCommandRequest> context)
    {
        var id = backgroundJobClient.Enqueue<IImportModelJob>(job =>
            job.ImportRtAsync(context.Message.TenantId, context.Message.CacheFileKey, BotCancellationToken.Null));
        
        await context.RespondAsync(new JobCreatedResponse(id));
    }

    public async Task ConsumeAsync(IDistributedContext<ExportRtByQueryCommandRequest> context)
    {
        var id = backgroundJobClient.Enqueue<IExportModelJob>(job =>
            job.ExportRtModelByQueryAsync(context.Message, BotCancellationToken.Null));
        
        await context.RespondAsync(new JobCreatedResponse(id));  
    }

    public async Task ConsumeAsync(IDistributedContext<ExportRtByDeepGraphCommandRequest> context)
    {
        var id = backgroundJobClient.Enqueue<IExportModelJob>(job =>
            job.ExportRtModelByDeepGraphAsync(context.Message, BotCancellationToken.Null));
        
        await context.RespondAsync(new JobCreatedResponse(id));  
    }
}