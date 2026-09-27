using System.Text.Json;
using System.Text.RegularExpressions;
namespace DevHarbor.AgentBridge;

public sealed class AgentRequestException(string code) : IOException(code) { public string Code { get; } = code; }
public sealed record AgentRequest(string Tool, JsonElement Arguments);
public static class AgentContract
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 16, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static string[] Names => ["devharbor_overview", "devharbor_items", "devharbor_plan", "devharbor_plan_status"];
    public static void Validate(AgentRequest request)
    {
        if (!Names.Contains(request.Tool)) throw new AgentRequestException("UnknownTool");
        var args = request.Arguments;
        if (args.ValueKind != JsonValueKind.Object || args.GetRawText().Length > 8192) throw new AgentRequestException("InvalidArguments");
        string[] allowed = request.Tool switch { "devharbor_items" => ["cursor", "limit"], "devharbor_plan" => ["itemIds", "requestId"], "devharbor_plan_status" => ["planId"], _ => [] };
        var seen = new HashSet<string>();
        foreach (var property in args.EnumerateObject())
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name)) throw new AgentRequestException("UnexpectedArgument");
        if (args.TryGetProperty("limit", out var limit) && (limit.ValueKind != JsonValueKind.Number || !limit.TryGetInt32(out int size) || size < 1 || size > 50)) throw new AgentRequestException("InvalidLimit");
        if (args.TryGetProperty("cursor", out var cursor) && (cursor.ValueKind != JsonValueKind.String || !Regex.IsMatch(cursor.GetString()!, "^[a-f0-9]{32}:[0-9]{1,4}$"))) throw new AgentRequestException("InvalidCursor");
        if (request.Tool == "devharbor_plan")
        {
            Id(args, "requestId");
            if (!args.TryGetProperty("itemIds", out var ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() is < 1 or > 16) throw new AgentRequestException("InvalidItemIds");
            var unique = new HashSet<string>();
            foreach (var id in ids.EnumerateArray()) if (!ValidId(id) || !unique.Add(id.GetString()!)) throw new AgentRequestException("InvalidItemIds");
        }
        if (request.Tool == "devharbor_plan_status") Id(args, "planId");
    }
    public static string Id(JsonElement args, string property)
    {
        if (!args.TryGetProperty(property, out var value) || !ValidId(value)) throw new AgentRequestException("InvalidId");
        return value.GetString()!;
    }
    private static bool ValidId(JsonElement value) => value.ValueKind == JsonValueKind.String && Regex.IsMatch(value.GetString()!, "^[a-f0-9]{32}$");
    public static JsonElement Value(object value) => JsonSerializer.SerializeToElement(value, Json);
    public static JsonElement Error(string code) => Value(new { error = code, changed = false });
}
