using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.PrivateSyncPlay;

public static class HubWire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static JsonNode Node(object value) => JsonSerializer.SerializeToNode(value, Json)!;
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static Configuration Clone(Configuration value) => JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(value, Json), Json)!;
    public static string Revision(Configuration value) => string.IsNullOrEmpty(value.PartyPolicyRevision)
        ? Hash(JsonSerializer.Serialize(value, Json)) : value.PartyPolicyRevision;
    public static object Success(object data) => new { apiVersion = "1", ok = true, data };
    public static object Failure(string code, string message, bool retryable = false) => new { apiVersion = "1", ok = false, error = new { code, message, retryable } };
}

public sealed class HubCommandReceipt
{
    public Guid Actor { get; set; }
    public Guid CommandId { get; set; }
    public string Operation { get; set; } = "";
    public string Hash { get; set; } = "";
    public string Result { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public sealed class HubAdapterException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
