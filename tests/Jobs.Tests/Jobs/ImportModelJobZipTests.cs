using System.IO.Compression;
using System.Text;
using Meshmakers.Common.Shared.Services;
using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Common.DistributionEventHub.Payloads;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs;

public class ImportModelJobZipTests
{
    private const string TenantId = "tenant-1";
    private const string CacheKey = "key-1";
    private const string JsonModel = "{\"model\":\"json\"}";
    private const string YamlModel = "model: yaml";

    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();
    private readonly IDistributedCacheService _cacheService = Substitute.For<IDistributedCacheService>();
    private readonly IImportCkModelCommand _importCkModelCommand = Substitute.For<IImportCkModelCommand>();
    private readonly IImportRtModelCommand _importRtModelCommand = Substitute.For<IImportRtModelCommand>();
    private readonly ITenantRepository _tenantRepository = Substitute.For<ITenantRepository>();

    private string? _importedContent;
    private string? _importedContentType;

    private ImportModelJob CreateJobForZip(string zipContentType, params (string EntryName, string Content)[] entries)
    {
        _cacheService.GetCacheStreamByIdAsync(TenantId, CacheKey).Returns(new CacheStream
        {
            Stream = CreateZip(entries), ContentType = zipContentType, FileName = "model.zip"
        });
        _systemContext.FindTenantRepositoryAsync(TenantId).Returns(_tenantRepository);
        _importRtModelCommand.BlueprintLockSummary.Returns(RtImportBlueprintLockSummary.Empty);

        _importCkModelCommand
            .When(command => command.ImportAsync(TenantId, Arg.Any<string>(), Arg.Any<CancellationToken?>()))
            .Do(call => _importedContent = File.ReadAllText(call.ArgAt<string>(1)));
        _importRtModelCommand
            .When(command => command.ImportAsCallerAsync(_tenantRepository, Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<ImportStrategy>(), Arg.Any<RtImportCallerContext>(), Arg.Any<CancellationToken?>(),
                Arg.Any<RtImportBlankingPolicy>(), Arg.Any<IReadOnlyCollection<RtImportBlankingConfirmation>?>()))
            .Do(call =>
            {
                _importedContent = File.ReadAllText(call.ArgAt<string>(1));
                _importedContentType = call.ArgAt<string>(2);
            });

        return new ImportModelJob(Substitute.For<ILogger<ImportModelJob>>(), _systemContext, _cacheService,
            new CompressionService(), _importCkModelCommand, _importRtModelCommand);
    }

    private static MemoryStream CreateZip((string EntryName, string Content)[] entries)
    {
        var zipStream = new MemoryStream();
        using (var zipArchive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (entryName, content) in entries)
            {
                using var entryWriter = new StreamWriter(zipArchive.CreateEntry(entryName).Open(), Encoding.UTF8);
                entryWriter.Write(content);
            }
        }

        zipStream.Position = 0;
        return zipStream;
    }

    [Test]
    [Arguments(MimeTypes.MimeTypeZip)]
    [Arguments(MimeTypes.MimeTypeXZipCompressed)]
    public async Task ImportRtAsync_ZipWithJsonEntry_ImportsTheJsonEntryAsJson(string zipContentType)
    {
        var job = CreateJobForZip(zipContentType, ("model.json", JsonModel));

        await job.ImportRtAsync(TenantId, ImportStrategy.Upsert, CacheKey, "user-sub-1", null);

        await Assert.That(_importedContent).IsEqualTo(JsonModel);
        await Assert.That(_importedContentType).IsEqualTo(MimeTypes.MimeTypeJson);
    }

    [Test]
    public async Task ImportCkAsync_ZipWithJsonEntry_ImportsTheJsonEntry()
    {
        var job = CreateJobForZip(MimeTypes.MimeTypeZip, ("ck-model.json", JsonModel));

        await job.ImportCkAsync(TenantId, CacheKey, null);

        await Assert.That(_importedContent).IsEqualTo(JsonModel);
    }

    [Test]
    public async Task ImportRtAsync_ZipWithJsonAndYamlEntries_PrefersTheJsonEntry()
    {
        var job = CreateJobForZip(MimeTypes.MimeTypeZip, ("model.yaml", YamlModel), ("model.json", JsonModel));

        await job.ImportRtAsync(TenantId, ImportStrategy.Upsert, CacheKey, "user-sub-1", null);

        await Assert.That(_importedContent).IsEqualTo(JsonModel);
        await Assert.That(_importedContentType).IsEqualTo(MimeTypes.MimeTypeJson);
    }

    [Test]
    [Arguments("model.yaml")]
    [Arguments("model.yml")]
    public async Task ImportRtAsync_ZipWithYamlEntry_ImportsTheYamlEntryAsYaml(string entryName)
    {
        var job = CreateJobForZip(MimeTypes.MimeTypeZip, (entryName, YamlModel));

        await job.ImportRtAsync(TenantId, ImportStrategy.Upsert, CacheKey, "user-sub-1", null);

        await Assert.That(_importedContent).IsEqualTo(YamlModel);
        await Assert.That(_importedContentType).IsEqualTo(MimeTypes.MimeTypeYaml);
    }

    [Test]
    public async Task ImportRtAsync_ZipWithoutModelEntry_FailsNamingTheAcceptedExtensions()
    {
        var job = CreateJobForZip(MimeTypes.MimeTypeZip, ("readme.txt", "not a model"));

        var failure = await Assert.That(async () =>
                await job.ImportRtAsync(TenantId, ImportStrategy.Upsert, CacheKey, "user-sub-1", null))
            .Throws<JobFailedException>();

        await Assert.That(failure!.Message).Contains(".json, .yaml or .yml");
        await Assert.That(_importedContent).IsNull();
    }
}
