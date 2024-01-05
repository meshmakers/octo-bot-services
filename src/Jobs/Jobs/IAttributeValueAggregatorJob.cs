namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
///     Interface for aggregating attribute values
/// </summary>
public interface IAttributeValueAggregatorJob
{
    /// <summary>
    ///     Aggregates values of attributes that are configured.
    /// </summary>
    /// <param name="tenantId">The corresponding data source</param>
    /// <param name="cancellationToken">An cancellation token to abort the job</param>
    /// <returns></returns>
    Task Run(string tenantId, IBotCancellationToken? cancellationToken);
}