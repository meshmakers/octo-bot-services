using System.Reflection;
using System.Runtime.CompilerServices;
using Hangfire;
using Meshmakers.Common.Shared.Services;
using Meshmakers.Octo.Backend.BotServices;
using Meshmakers.Octo.Backend.BotServices.Consumers;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.Jobs.IndexUpgrade;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.IndexUpgrade;

/// <summary>
///     AB#9118 — the CK import gate (AC 6) and the tenant-created trigger (AC 5) reach production through optional
///     constructor parameters. These tests resolve both classes from the service registrations the host uses
///     (<c>AddOctoJobs</c> + <c>AddOctoIndexUpgrade</c>, host services as substitutes) and pin the call in
///     <c>Program.cs</c>, the convention of <c>TenantAuthorizationWiringTests</c>.
/// </summary>
public class IndexUpgradeWiringTests
{
    [Test]
    public async Task ImportModelJob_FromTheHostRegistrations_GetsTheImportGate()
    {
        await using var provider = HostServices();

        var job = provider.GetRequiredService<IImportModelJob>();

        await Assert.That(Field<ICkImportIndexUpgradeGate>(job, "_indexUpgradeGate")).IsNotNull();
    }

    [Test]
    public async Task TenantManagementConsumer_FromTheHostRegistrations_GetsTheScheduler()
    {
        await using var provider = HostServices();

        // The event hub creates consumers from the container the same way.
        var consumer = ActivatorUtilities.CreateInstance<TenantManagementConsumer>(provider);

        await Assert.That(Field<IIndexUpgradeScheduler>(consumer, "_indexUpgradeScheduler")).IsNotNull();
    }

    [Test]
    public async Task HostRegistrations_ContainTheStartupRegistration()
    {
        await using var provider = HostServices();

        await Assert.That(provider.GetServices<IHostedService>().OfType<IndexUpgradeStartupService>()).IsNotEmpty();
    }

    [Test]
    public async Task Program_RegistersTheIndexUpgrade_AndItsServer()
    {
        var program = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "src", "BotServices", "Program.cs"));

        await Assert.That(RegistersTheIndexUpgrade(program)).IsTrue();
    }

    [Test]
    [Arguments("// builder.Services.AddOctoIndexUpgrade(builder.Configuration);\noptions.Queues = [IndexUpgradeConstants.Queue];")]
    [Arguments("builder.Services.AddOctoIndexUpgrade(builder.Configuration);\n// options.Queues = [IndexUpgradeConstants.Queue];")]
    [Arguments("/* builder.Services.AddOctoIndexUpgrade(builder.Configuration); */\noptions.Queues = [IndexUpgradeConstants.Queue];")]
    [Arguments("/*\nbuilder.Services.AddOctoIndexUpgrade(builder.Configuration);\noptions.Queues = [IndexUpgradeConstants.Queue];\n*/")]
    [Arguments("var a = 1; // builder.Services.AddOctoIndexUpgrade(builder.Configuration);\noptions.Queues = [IndexUpgradeConstants.Queue];")]
    public async Task ProgramCheck_CommentedOutRegistration_Fails(string program)
    {
        // AB#9317: a commented-out line must not satisfy the wiring check.
        await Assert.That(RegistersTheIndexUpgrade(program)).IsFalse();
    }

    [Test]
    public async Task ProgramCheck_CommentMarkersInStrings_DoNotHideCode()
    {
        const string program = "var url = \"https://x/*\"; var c = '/'; var v = @\"a\"\"//\";\n" +
                               "builder.Services.AddOctoIndexUpgrade(builder.Configuration); // AB#9118\n" +
                               "options.Queues = [IndexUpgradeConstants.Queue]; /* the queue */";

        await Assert.That(RegistersTheIndexUpgrade(program)).IsTrue();
    }

    private static bool RegistersTheIndexUpgrade(string program)
    {
        var code = WithoutComments(program);
        return code.Contains("builder.Services.AddOctoIndexUpgrade(builder.Configuration);", StringComparison.Ordinal) &&
               code.Contains("options.Queues = [IndexUpgradeConstants.Queue];", StringComparison.Ordinal);
    }

    /// <summary>
    ///     The C# source without <c>//</c> and <c>/* */</c> comments; regular and verbatim string literals and char
    ///     literals are kept as they are, so a <c>//</c> inside a URL is not a comment. Enough for <c>Program.cs</c>, not
    ///     a full C# lexer (raw and interpolated strings with nested quotes are not modelled).
    /// </summary>
    internal static string WithoutComments(string source)
    {
        var result = new System.Text.StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && next == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 2;
                result.Append(' ');
            }
            else if (c == '"' || c == '\'')
            {
                var verbatim = c == '"' && i > 0 && source[i - 1] == '@';
                var start = i++;
                while (i < source.Length)
                {
                    if (!verbatim && source[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (source[i] == c)
                    {
                        if (verbatim && i + 1 < source.Length && source[i + 1] == '"')
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    if (!verbatim && source[i] == '\n')
                    {
                        break;
                    }

                    i++;
                }

                result.Append(source, start, Math.Min(i, source.Length) - start);
            }
            else
            {
                result.Append(c);
                i++;
            }
        }

        return result.ToString();
    }

    private static ServiceProvider HostServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.Configure<OctoSystemConfiguration>(_ => { });
        services.Configure<OctoBotServicesOptions>(_ => { });
        // Services of the engine, the event hub and the host that the classes under test need.
        services.AddSingleton(Substitute.For<ISystemContext>());
        services.AddSingleton(Substitute.For<IDistributedCacheService>());
        services.AddSingleton(Substitute.For<IImportRtModelCommand>());
        services.AddSingleton(Substitute.For<ICkSerializer>());
        services.AddSingleton(Substitute.For<IBackgroundJobClient>());
        services.AddSingleton(Substitute.For<IRecurringJobManager>());
        services.AddSingleton(Substitute.For<ICkMethodOperationTenantPurge>());
        services.AddSingleton(Substitute.For<ITenantLifecycleStore>());
        services.AddSingleton<IJobCreatorService>(new NoJobCreator());
        services.AddSingleton(Substitute.For<ICompressionService>());
        services.AddOctoJobs();
        services.AddOctoIndexUpgrade(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }

    private static T? Field<T>(object instance, string name) where T : class =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) as T;

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));

    private sealed class NoJobCreator : IJobCreatorService
    {
        public void CreateJobs(string instancePrefix, string tenantId)
        {
        }

        public void DeleteJobs(string instancePrefix, string tenantId)
        {
        }
    }
}
