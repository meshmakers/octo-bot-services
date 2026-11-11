using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshmakers.Octo.ConstructionKit.Models.System.Bot.Generated.System.Bot.v3;

/// <summary>
///     AB#8447: the result record of <c>TenantRepository.Restore-1</c> is serialized as the bare camel-case object of its
///     members (method wire contract, AB#8079), like <see cref="RtRepositoryDumpResultRecord" />.
/// </summary>
[JsonConverter(typeof(RtRecordWireJsonConverter<RtRepositoryRestoreResultRecord>))]
public partial class RtRepositoryRestoreResultRecord;

/// <summary>AB#8447: wire form of the result record of <c>TenantRepository.ExportArchiveData-1</c>.</summary>
[JsonConverter(typeof(RtRecordWireJsonConverter<RtArchiveDataExportResultRecord>))]
public partial class RtArchiveDataExportResultRecord;

/// <summary>AB#8447: wire form of the result record of <c>TenantRepository.ImportArchiveData-1</c>.</summary>
[JsonConverter(typeof(RtRecordWireJsonConverter<RtArchiveDataImportResultRecord>))]
public partial class RtArchiveDataImportResultRecord;

/// <summary>
///     System.Text.Json converter that writes a generated CK record as the bare object of its members, camel-case, in
///     declaration order (<c>{"jobId":…,"rowCount":…}</c>), and reads that form back, instead of the generic shape of
///     <c>RtRecord</c> (CK record id, attribute dictionary). Only the public read/write properties the generator declares
///     on <typeparamref name="TRecord" /> itself are members; unknown JSON properties are ignored.
/// </summary>
/// <typeparam name="TRecord">The generated record class.</typeparam>
public sealed class RtRecordWireJsonConverter<TRecord> : JsonConverter<TRecord> where TRecord : class, new()
{
    private static readonly PropertyInfo[] Members = typeof(TRecord)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(p => p is { CanRead: true, CanWrite: true } && p.GetIndexParameters().Length == 0)
        .ToArray();

    /// <inheritdoc />
    public override TRecord? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var record = new TRecord();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var member = Members.FirstOrDefault(m =>
                string.Equals(m.Name, property.Name, StringComparison.OrdinalIgnoreCase));
            if (member == null || property.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            member.SetValue(record, property.Value.Deserialize(member.PropertyType, options));
        }

        return record;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TRecord value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var member in Members)
        {
            writer.WritePropertyName(JsonNamingPolicy.CamelCase.ConvertName(member.Name));
            JsonSerializer.Serialize(writer, member.GetValue(value), member.PropertyType, options);
        }

        writer.WriteEndObject();
    }
}
