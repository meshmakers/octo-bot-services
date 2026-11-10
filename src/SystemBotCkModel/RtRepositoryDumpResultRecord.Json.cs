using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshmakers.Octo.ConstructionKit.Models.System.Bot.Generated.System.Bot.v3;

/// <summary>
///     AB#8445: the result record of <c>TenantRepository.Dump-1</c> is serialized as the bare camel-case object of its
///     members (<c>{"jobId":…,"downloadPath":…}</c>), the wire form of a record result (method wire contract, AB#8079),
///     instead of the generic shape of <c>RtRecord</c> (CK record id, attribute dictionary).
/// </summary>
[JsonConverter(typeof(RtRepositoryDumpResultRecordJsonConverter))]
public partial class RtRepositoryDumpResultRecord;

/// <summary>System.Text.Json converter of <see cref="RtRepositoryDumpResultRecord" />.</summary>
public sealed class RtRepositoryDumpResultRecordJsonConverter : JsonConverter<RtRepositoryDumpResultRecord>
{
    private const string JobIdName = "jobId";
    private const string DownloadPathName = "downloadPath";

    /// <inheritdoc />
    public override RtRepositoryDumpResultRecord? Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var record = new RtRepositoryDumpResultRecord();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (string.Equals(property.Name, JobIdName, StringComparison.OrdinalIgnoreCase))
            {
                record.JobId = property.Value.GetString()!;
            }
            else if (string.Equals(property.Name, DownloadPathName, StringComparison.OrdinalIgnoreCase))
            {
                record.DownloadPath = property.Value.GetString()!;
            }
        }

        return record;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, RtRepositoryDumpResultRecord value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString(JobIdName, value.JobId);
        writer.WriteString(DownloadPathName, value.DownloadPath);
        writer.WriteEndObject();
    }
}
