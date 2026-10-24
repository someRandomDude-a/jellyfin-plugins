using System.Text.Json;

namespace AccountHub.HostFixture;

public static class Host
{
    public static Func<string, CancellationToken, Task<string>> Create()
    {
        var accepted = new Dictionary<string, string>();
        return (json, _) =>
        {
            using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
            var operation = root.GetProperty("operation").GetString();
            object data;
            if (operation == "hello") data = new { hostVersion = "fixture", apiVersion = "1", operations = new[] { "notifications.publish", "audit.publish" } };
            else if (operation == "health") data = new { ready = true, hostVersion = "fixture", schemaVersion = 1, features = Array.Empty<string>() };
            else
            {
                var payload = root.GetProperty("payload"); var id = payload.GetProperty("eventId").GetString()!;
                var duplicate = accepted.TryGetValue(id, out var previous);
                if (duplicate && previous != payload.GetRawText()) return Task.FromResult("{\"apiVersion\":\"1\",\"ok\":false,\"error\":{\"code\":\"rejected\"}}");
                accepted[id] = payload.GetRawText(); data = new { status = duplicate ? "duplicate" : "accepted", notificationIds = new[] { id } };
            }
            return Task.FromResult(JsonSerializer.Serialize(new { apiVersion = "1", ok = true, data }));
        };
    }
}
