using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccountHub.Api.Contracts;

namespace AccountHub.Core;

public static class CanonicalJson
{
    // Object ordering does not turn a legitimate retry into a conflicting event/command.
    public static string Serialize<T>(T value) => Normalize(JsonSerializer.SerializeToElement(value, Wire.Options));
    public static string Normalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, element);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            var properties = element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
            if (properties.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                throw new HubException("invalid_request", "Duplicate JSON properties are not allowed");
            foreach (var property in properties)
            {
                writer.WritePropertyName(property.Name);
                Write(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray()) Write(writer, item);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }
}
