using System.Text;
using System.Text.Json.Nodes;

namespace SGL.Core;

public sealed class MapTags(string path)
{
    public const int MaximumTags = 20, MaximumTagLength = 40;
    public static string[] Parse(string text) => Normalize(text.Split([',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    public static string[] Normalize(IEnumerable<string> values)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string value in values)
        {
            string tag = string.Join(" ", value.Normalize(NormalizationForm.FormC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (tag.Length == 0) continue;
            if (tag.Length > MaximumTagLength || tag.Any(char.IsControl) || tag.Contains(','))
                throw new InvalidDataException($"Use tags of up to {MaximumTagLength} characters, without commas or control characters.");
            tags.Add(tag);
            if (tags.Count > MaximumTags)
                throw new InvalidDataException($"Use at most {MaximumTags} tags per map.");
        }
        return tags.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public Dictionary<string, string[]> Read()
    {
        if (!File.Exists(path)) return new(StringComparer.Ordinal);
        if (new FileInfo(path).Length > 1_048_576)
            throw new InvalidDataException("Local map tags exceed 1 MB.");
        var doc = Json.Read(File.ReadAllText(path)).AsObject();
        if ((int?)doc["version"] != 1 || doc["maps"] is not JsonObject maps)
            throw new InvalidDataException("Unsupported local map tag file.");
        return maps.ToDictionary(p => p.Key,
            p => Normalize(p.Value!.AsArray().Select(t => t!.GetValue<string>())), StringComparer.Ordinal);
    }
    public void Set(string mapId, IEnumerable<string> tags)
    {
        if (string.IsNullOrWhiteSpace(mapId) || mapId.Length > 180)
            throw new InvalidDataException("Invalid map identity for local tags.");
        var normalized = Normalize(tags);
        var all = Read();
        if (normalized.Length == 0) all.Remove(mapId);
        else all[mapId] = normalized;
        var maps = new JsonObject(all.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key, new JsonArray(p.Value.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()))));
        string content = new JsonObject { ["version"] = 1, ["maps"] = maps }.ToJsonString();
        if (Encoding.UTF8.GetByteCount(content) > 1_048_576)
            throw new InvalidDataException("Local map tags exceed 1 MB.");
        Json.Atomic(path, content);
    }
    public static bool Matches(IEnumerable<string> tags, string search, string? selectedTag = null)
    {
        var values = tags.ToArray();
        return (selectedTag == null || values.Contains(selectedTag, StringComparer.OrdinalIgnoreCase))
            && (search.Length == 0 || values.Any(t => t.Contains(search, StringComparison.OrdinalIgnoreCase)));
    }
    public bool ImportShared(string mapId, IEnumerable<string> incoming, IEnumerable<string>? previousShared)
    {
        var current = Read().GetValueOrDefault(mapId) ?? [];
        // A local addition or removal is a host preference; retain it on updates.
        if (previousShared != null && !SharedTags.Equal(current, previousShared)) return false;
        Set(mapId, incoming);
        return true;
    }
}
