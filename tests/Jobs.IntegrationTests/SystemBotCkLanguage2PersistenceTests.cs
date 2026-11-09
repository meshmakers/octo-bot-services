using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.Jobs.IntegrationTests;

/// <summary>
///     AB#8593: System.Bot 3.6.0 is the first ckLanguage 2 release. The CK cache of a tenant is rebuilt from the model
///     stored in MongoDB, not from the compiled file, so the explicit <c>derivable: Any</c> must survive the import and the
///     read-back; if it were lost, the cache would apply the ckLanguage 2 default <c>Model</c> and every model deriving from a
///     System.Bot type would stop resolving. A second import of the same version (a service restart) must be a no-op.
/// </summary>
[NotInParallel]
public class SystemBotCkLanguage2PersistenceTests
{
    [Test]
    public async Task ImportedSystemBot_KeepsDerivableAny_AndAReImportIsANoOp()
    {
        var fixture = await ExportEngineFixture.GetAsync();
        await fixture.ResetAsync();

        foreach (var attempt in new[] { "first import", "re-import (service restart)" })
        {
            var operationResult = new OperationResult();
            await fixture.SystemContext.ImportCkModelAsync(new CkModelId("System.Bot-3.6.0"), operationResult);
            await Assert.That(operationResult.HasErrors).IsFalse().Because($"{attempt}: {operationResult}");
        }

        var cache = fixture.Provider.GetRequiredService<ICkCacheService>();
        foreach (var typeId in new[] { "System.Bot/Fixup", "System.Bot/AttributeAggregateConfiguration" })
        {
            var type = cache.GetRtCkType(fixture.TenantId, new RtCkId<CkTypeId>(typeId));
            await Assert.That(type.Derivable).IsEqualTo(CkDerivableDto.Any).Because(typeId);
        }
    }
}
