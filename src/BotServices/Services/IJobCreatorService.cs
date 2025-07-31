namespace Meshmakers.Octo.Backend.BotServices.Services;

internal interface IJobCreatorService
{
    void CreateJobs(string instancePrefix, string tenantId);
    void DeleteJobs(string instancePrefix, string tenantId);
}