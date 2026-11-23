using System.Text;
using System.Text.Json.Nodes;

namespace SGL.Core;

public static class SharedTags
{
    public static string[] ReadNode(JsonNode? node)
    {
        if (node == null) return [];
        if (node is not JsonArray values || values.Count > MapTags.MaximumTags || values.Any(v => v is not JsonValue j || !j.TryGetValue<string>(out _)))
            throw new InvalidDataException("Shared map tags must be a list of up to 20 text labels.");
        return MapTags.Normalize(values.Select(v => v!.GetValue<string>()));
    }
    public static string[] Read(JsonObject attribution) => ReadNode(attribution["tags"]
        ?? attribution["revision"]?["metadata"]?["tags"] ?? attribution["item"]?["tags"]);
    public static JsonArray Array(IEnumerable<string> tags) => new(MapTags.Normalize(tags).Select(t => (JsonNode)JsonValue.Create(t)!).ToArray());
    public static string PublicationHash(byte[] content, IEnumerable<string> tags) => Json.Hash(Encoding.UTF8.GetBytes(
        new JsonObject { ["content"] = Json.Hash(content), ["tags"] = Array(tags) }.ToJsonString()));
    public static bool Equal(IEnumerable<string> a, IEnumerable<string> b) => a.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b);
}
