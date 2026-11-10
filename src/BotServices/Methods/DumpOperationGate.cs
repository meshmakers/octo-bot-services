using Meshmakers.Octo.Backend.Jobs.Jobs.Operations;
using Meshmakers.Octo.Services.Methods.Operations;

namespace Meshmakers.Octo.Backend.BotServices.Methods;

/// <summary>
///     <see cref="IDumpOperationGate" /> on the operation store of the method runtime (AB#8443, AB#8445): a dump job
///     started by <c>TenantRepository.Dump-1</c> runs only while its operation is <c>Accepted</c> or <c>Running</c>.
/// </summary>
/// <remarks>
///     Fails closed: an unknown or expired operation, or one of another tenant, is "not open". A store that cannot be read
///     throws, so the job fails and nothing is dumped; the work of the operation then answers <c>DUMP_FAILED</c>.
/// </remarks>
internal sealed class DumpOperationGate(ICkMethodOperationStore store) : IDumpOperationGate
{
    public async Task<bool> IsOperationOpenAsync(string tenantId, string operationId,
        CancellationToken cancellationToken)
    {
        var record = await store.GetAsync(tenantId, operationId, cancellationToken);
        return record is { State: CkMethodOperationState.Accepted or CkMethodOperationState.Running };
    }
}
