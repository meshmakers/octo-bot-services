using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Meshmakers.Octo.Backend.BotServices.Services;

internal class StartupService : IHostedService
{
    private readonly IServiceHookService _serviceHookService;
    private readonly IUserSchemaService _userSchemaService;

    public StartupService(IUserSchemaService userSchemaService, IServiceHookService serviceHookService)
    {
        _userSchemaService = userSchemaService;
        _serviceHookService = serviceHookService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _userSchemaService.SetupAsync();
        await _serviceHookService.SyncDataSourceAndCreateJobsAsync();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
