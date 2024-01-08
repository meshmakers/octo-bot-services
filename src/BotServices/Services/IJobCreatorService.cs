namespace Meshmakers.Octo.Backend.BotServices.Services;

internal interface IJobCreatorService
{
    void CreateJobs(string tenantId);
    void DeleteJobs(string tenantId);
}