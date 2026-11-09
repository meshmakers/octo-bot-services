using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelCatalogs;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.Jobs.Tests.CkModels;

/// <summary>
///     AB#8593 (Feature AB#5697 F4.3): System.Bot 3.6.0 is the first ckLanguage 2 release of the model and a MINOR
///     against the published 3.5.0. The tests read the compiled model the service embeds (the one it imports into every
///     tenant) and guard the three properties the Minor depends on.
/// </summary>
public class SystemBotCkLanguage2Tests
{
    private static async Task<CkCompiledModelRoot> LoadEmbeddedSystemBotAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRuntimeEngine();
        services.AddCkModelSystemBotV3();
        await using var provider = services.BuildServiceProvider();

        var embedded = provider.GetServices<ICkEmbeddedModel>().Single(m => m.ModelId.Name == "System.Bot");
        var operationResult = new OperationResult();
        var root = await embedded.GetCompiledModelRootAsync(operationResult);
        await Assert.That(operationResult.HasErrors).IsFalse();
        return root;
    }

    [Test]
    public async Task EmbeddedModel_IsSystemBot_3_6_0_OnCkLanguage2_WithTheCkV2MinEngineVersion()
    {
        var root = await LoadEmbeddedSystemBotAsync();

        await Assert.That(root.ModelId.ToString()).IsEqualTo("System.Bot-3.6.0");
        await Assert.That(root.CkLanguage).IsEqualTo(2);
        // A ckLanguage 2 model always carries a minEngineVersion, so an engine without CK v2 refuses it (message 126)
        // instead of reading it as a classic model; the compiler stamps it, the sources never set it. Pinned exactly: a
        // higher value means every service below it can no longer import System.Bot. A feature that raises it (e.g. a
        // method with execution.mode, AB#8445) is a deliberate rollout decision and updates this assertion.
        await Assert.That(root.MinEngineVersion).IsNotNull();
        await Assert.That(Version.Parse(root.MinEngineVersion!)).IsEqualTo(new Version(3, 5, 1));
    }

    /// <summary>
    ///     ckLanguage 2 changes the default of <c>derivable</c> from Any to Model (row T6 of ck-semver-rules.md). Without
    ///     the explicit <c>derivable: Any</c> every pre-existing type would be a MAJOR change (OCTO-CK200), and a model
    ///     deriving from it would stop compiling (message 113).
    /// </summary>
    [Test]
    public async Task EveryPreExistingType_StaysDerivableEverywhere()
    {
        var root = await LoadEmbeddedSystemBotAsync();
        var types = root.Types ?? [];

        foreach (var typeId in new[] { "AttributeAggregateConfiguration", "Fixup" })
        {
            var type = types.SingleOrDefault(t => t.TypeId.Name == typeId);
            await Assert.That(type).IsNotNull();
            await Assert.That(type!.Derivable).IsEqualTo(CkDerivableDto.Any);
        }
    }

    /// <summary>
    ///     No attribute is tightened in 3.6.0: tightening an attribute that is not <c>securitySensitive</c> is a MAJOR
    ///     (row T7), and the Fixup attributes are written through the generic GraphQL API by Refinery Studio and octo-cli.
    ///     Tightening one of them later is a deliberate, versioned decision that must update this test.
    /// </summary>
    [Test]
    public async Task NoAttributeAssignment_IsTightened()
    {
        var root = await LoadEmbeddedSystemBotAsync();

        var tightened = (root.Types ?? [])
            .SelectMany(t => (t.Attributes ?? []).Select(a => (Type: t.TypeId.Name, Attribute: a)))
            .Where(x => x.Attribute.Access is not null && x.Attribute.Access != CkAttributeAccessDto.ReadWrite)
            .Select(x => $"{x.Type}.{x.Attribute.AttributeName}")
            .ToList();

        await Assert.That(tightened).IsEmpty();
    }
}
