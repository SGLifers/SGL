using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;
namespace SGL.Core;

public static class Configuration
{
    public static readonly JsonObject Schema = (JsonObject)Json.Read(Modes.Resource("settings-schema.json"));
    public static readonly JsonObject Catalog = (JsonObject)Json.Read(Modes.Resource("catalog.json"));
    public static IEnumerable<JsonObject> Fields => Schema["fields"]!.AsArray().Cast<JsonObject>();
    public static JsonObject Clone(JsonObject value) => (JsonObject)value.DeepClone();
    public static JsonObject ReadArchive(byte[] bytes)
    {
        if (bytes.Length < 2 || bytes[0] != 1 || bytes.Length > 8_000_000)
            throw new InvalidDataException("Invalid mode archive.");
        using var stream = new MemoryStream(bytes, 1, bytes.Length - 1);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = zip.GetEntry("payload.json") ?? throw new InvalidDataException("Missing payload.json.");
        if (entry.Length > 1_048_576)
            throw new InvalidDataException("Payload exceeds 1 MB.");
        using var reader = new StreamReader(entry.Open());
        return (JsonObject)Json.Read(reader.ReadToEnd());
    }
    public static Dictionary<string, object> Wrapped(JsonObject payload) => Modes.Recipes(Modes.Parse(payload.ToJsonString()))[0];
    public static JsonObject New(string basis) => BaseModes.Create(basis);
    public static IEnumerable<JsonObject> Contexts(JsonObject payload) => payload["Options"]!["OptionMap"]!.AsObject().Select(n => n.Value!["Options"]!.AsObject());
    // The game stores global options, playable rules, and inherited round states separately.
    // A phase edit updates its state descendants, stopping at the other playable phase.
    public static IReadOnlyList<RuleContext> Rules(JsonObject payload)
    {
        var map = payload["Options"]!["OptionMap"]!.AsObject();
        var profile = GamemodeUi.Profile(payload);
        if (profile != null && map.ContainsKey((string)profile["standardContext"]!) && map.ContainsKey((string)profile["tiebreakerContext"]!))
            return [new((string)profile["standardContext"]!, "Standard Rules"), new((string)profile["tiebreakerContext"]!, "Tiebreaker")];
        var root = map.Where(c => c.Value?["InheritsFrom"] == null).OrderByDescending(c => c.Value?["Options"]?.AsObject().Count ?? 0).First();
        var tie = map.Where(c => (string?)c.Value?["InheritsFrom"] == root.Key).OrderByDescending(c => c.Value?["Options"]?.AsObject().Count ?? 0).FirstOrDefault();
        return tie.Value == null ? [new(root.Key, "Standard Rules")] : [new(root.Key, "Standard Rules"), new(tie.Key, "Tiebreaker")];
    }
    static IEnumerable<JsonObject> ReadContexts(JsonObject payload, string? contextId)
    {
        if (contextId == null) return Contexts(payload);
        var map = payload["Options"]!["OptionMap"]!.AsObject();
        if (!map.ContainsKey(contextId)) throw new InvalidDataException("Unknown rule context.");
        var result = new List<JsonObject>();
        var seen = new HashSet<string>();
        string? id = contextId;
        while (id != null && seen.Add(id) && map[id] is JsonObject context)
        {
            result.Add(context["Options"]!.AsObject());
            id = (string?)context["InheritsFrom"];
        }
        // Global-only options (round count, match score, team switching) apply to both phases.
        foreach (var pair in map.Where(c => c.Value?["InheritsFrom"] == null && !seen.Contains(c.Key)))
            result.Add(pair.Value!["Options"]!.AsObject());
        return result;
    }
    static HashSet<string> PhaseContextIds(JsonObject payload, string contextId)
    {
        var map = payload["Options"]!["OptionMap"]!.AsObject();
        if (!map.ContainsKey(contextId)) throw new InvalidDataException("Unknown rule context.");
        var phases = Rules(payload).Select(c => c.Id).ToHashSet();
        var ids = new HashSet<string> { contextId };
        bool added;
        do
        {
            added = false;
            foreach (var pair in map)
                if (!phases.Contains(pair.Key) && (string?)pair.Value?["InheritsFrom"] is string parent && ids.Contains(parent))
                    added |= ids.Add(pair.Key);
        } while (added);
        return ids;
    }
    static HashSet<string> TargetContextIds(JsonObject payload, JsonObject field, string contextId)
    {
        string option = (string)field["option"]!, property = (string)field["property"]!;
        var map = payload["Options"]!["OptionMap"]!.AsObject();
        if (!map.ContainsKey(contextId)) throw new InvalidDataException("Unknown rule context.");
        if (map[contextId]?["Options"]?[option]?[property] != null)
            return PhaseContextIds(payload, contextId);
        // A field that exists only globally must stay global rather than creating a fake phase option.
        var global = map.Where(c => c.Value?["InheritsFrom"] == null && c.Value?["Options"]?[option]?[property] != null).Select(c => c.Key).ToHashSet();
        if (global.Count > 0 && !Rules(payload).Any(r => global.Contains(r.Id))) return global;
        return PhaseContextIds(payload, contextId);
    }
    static IEnumerable<JsonObject> WriteContexts(JsonObject payload, JsonObject field, string? contextId) => contextId == null
        ? Contexts(payload)
        : TargetContextIds(payload, field, contextId).Select(id => payload["Options"]!["OptionMap"]![id]!["Options"]!.AsObject());
    public static bool IsShared(JsonObject payload, JsonObject field, string contextId) =>
        !TargetContextIds(payload, field, contextId).Contains(contextId);
    public static JsonNode? Value(JsonObject payload, JsonObject field, string? contextId = null)
    {
        string option = (string)field["option"]!, property = (string)field["property"]!;
        foreach (var options in ReadContexts(payload, contextId))
            if (options[option]?[property] is JsonNode value)
                return (bool)field["wrapped"]! ? value["defaultValue"]?.DeepClone() : value.DeepClone();
        return null;
    }
    public static void Set(JsonObject payload, JsonObject field, string text, string? contextId = null)
    {
        if ((string)field["id"]! == "weaponDrops" && text.Equals("true", StringComparison.OrdinalIgnoreCase) && Value(payload, Fields.Single(f => (string)f["id"]! == "infiniteReserves"), contextId)?.GetValue<bool>() == true)
            throw new InvalidDataException("Weapon drops must stay off while infinite reserves are enabled.");
        string type = (string)field["type"]!;
        JsonNode value;
        if (type == "boolean")
            value = JsonValue.Create(bool.Parse(text))!;
        else if (type == "enum")
        {
            if (!field["choices"]!.AsArray().Any(n => (string?)n!["value"] == text))
                throw new InvalidDataException($"Invalid {field["label"]} selection.");
            value = JsonValue.Create(text)!;
        }
        else
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || !double.IsFinite(n) || n < (double)field["min"]! || n > (double)field["max"]! || (type == "integer" && n != Math.Truncate(n)))
                throw new InvalidDataException($"Invalid {field["label"]}: allowed {field["min"]}–{field["max"]}.");
            value = JsonValue.Create(n)!;
        }
        string option = (string)field["option"]!, property = (string)field["property"]!;
        if (contextId != null && !IsShared(payload, field, contextId))
        {
            var selected = payload["Options"]!["OptionMap"]![contextId]!["Options"]!.AsObject();
            if (selected[option]?[property] == null)
            {
                var inherited = ReadContexts(payload, contextId).FirstOrDefault(o => o[option]?[property] != null)?[option];
                if (inherited?[property] != null)
                {
                    if (selected[option] is not JsonObject) selected[option] = new JsonObject { ["Version"] = inherited["Version"]?.DeepClone() ?? JsonValue.Create(0) };
                    selected[option]![property] = inherited[property]!.DeepClone();
                }
            }
        }
        int count = 0;
        foreach (var options in WriteContexts(payload, field, contextId))
            if (options[option] is JsonObject settings && settings[property] is JsonNode current)
            {
                if ((bool)field["wrapped"]!)
                    current["defaultValue"] = value.DeepClone();
                else
                    settings[property] = value.DeepClone();
                count++;
            }
        if (count == 0)
            throw new InvalidDataException("This base does not expose that setting.");
        if ((string)field["id"]! == "infiniteReserves" && text.Equals("true", StringComparison.OrdinalIgnoreCase))
            Set(payload, Fields.Single(f => (string)f["id"]! == "weaponDrops"), "false", contextId);
    }
    public static void Reset(JsonObject payload, JsonObject opened, JsonObject field, string? contextId = null)
    {
        string id = (string)field["id"]!, option = (string)field["option"]!, property = (string)field["property"]!;
        if (id == "weaponDrops" && Value(opened, field, contextId)?.GetValue<bool>() == true && Value(payload, Fields.Single(f => (string)f["id"]! == "infiniteReserves"), contextId)?.GetValue<bool>() == true)
            throw new InvalidDataException("Weapon drops must stay off while infinite reserves are enabled.");
        var targets = contextId == null ? payload["Options"]!["OptionMap"]!.AsObject().Select(c => c.Key).ToHashSet() : TargetContextIds(payload, field, contextId);
        foreach (string key in targets)
        {
            var options = payload["Options"]!["OptionMap"]![key]!["Options"]!.AsObject();
            if (options[option] is not JsonObject settings) continue;
            var originalOptions = opened["Options"]?["OptionMap"]?[key]?["Options"];
            if (originalOptions?[option]?[property] is JsonNode original) settings[property] = original.DeepClone();
            else if (originalOptions?[option] == null) options.Remove(option);
            else settings.Remove(property);
        }
        if (id == "infiniteReserves" && Value(payload, field, contextId)?.GetValue<bool>() == true)
            Set(payload, Fields.Single(f => (string)f["id"]! == "weaponDrops"), "false", contextId);
    }
    public static string[]? Loadout(JsonObject p, string? contextId = null)
    {
        foreach (var options in ReadContexts(p, contextId))
            if (options[Modes.Faction]?["CustomLoadouts"]?["customLoadouts"]?["None"]?["customLoadouts"] is JsonArray list && list.Count > 0)
            {
                var items = list[0]?["selectedItems"];
                if (items == null)
                    continue;
                return Slots.Select(s => items[s]?.GetValue<string>()?.Replace("LoadoutItem:", "") ?? "").ToArray();
            }
        return null;
    }
    public static readonly string[] Slots = ["LoadoutSlot.CustomWeapon.01.Weapon", "LoadoutSlot.CustomWeapon.02.Weapon", "LoadoutSlot.CustomEquipment"];
    public static void SetLoadout(JsonObject p, string[] ids, string? contextId = null)
    {
        for (int i = 0; i < 3; i++)
            if (!Catalog[i == 2 ? "equipment" : "weapons"]!.AsArray().Any(n => (string)n!["id"]! == ids[i]))
                throw new InvalidDataException("Unknown loadout item.");
        foreach (var options in contextId == null ? Contexts(p) : PhaseContextIds(p, contextId).Select(id => p["Options"]!["OptionMap"]![id]!["Options"]!.AsObject()))
            if (options[Modes.Faction] is JsonObject faction)
            {
                faction["bAllowLoadoutChangesDuringGameplay"] = false;
                // Explicit loadout edits replace starting selection only; all other options survive.
                var items = new JsonObject();
                for (int i = 0; i < 3; i++)
                    items[Slots[i]] = "LoadoutItem:" + ids[i];
                faction["CustomLoadouts"] = new JsonObject { ["customLoadouts"] = new JsonObject { ["None"] = new JsonObject { ["customLoadouts"] = new JsonArray(new JsonObject { ["selectedItems"] = items, ["randomItemTags"] = new JsonObject(), ["randomWeaponAttachmentSlots"] = new JsonObject() }) } } };
            }
    }
    public static string Name(JsonObject p) => (string?)p["Options"]?["CustomName"] ?? "Unnamed mode";
    public static bool Supported(JsonObject p) => p["Variant"] is JsonValue v && v.TryGetValue<string>(out var id) && Modes.VariantNames.ContainsKey(id);
    public static string Diff(JsonNode? before, JsonNode? after)
    {
        var lines = new List<string>();
        Walk("", before, after, lines);
        return lines.Count == 0 ? "No configuration changes." : string.Join(Environment.NewLine, lines.Distinct());
    }
    static void Walk(string path, JsonNode? a, JsonNode? b, List<string> lines)
    {
        if (JsonNode.DeepEquals(a, b))
            return;
        if (a is JsonObject x && b is JsonObject y)
        {
            foreach (string key in x.Select(v => v.Key).Union(y.Select(v => v.Key)).Order())
                Walk(path + "/" + key, x[key], y[key], lines);
            return;
        }
        if (a is JsonArray ax && b is JsonArray bx)
        {
            for (int i = 0; i < Math.Max(ax.Count, bx.Count); i++)
                Walk(path + $"/{i}", i < ax.Count ? ax[i] : null, i < bx.Count ? bx[i] : null, lines);
            return;
        }
        string label = path;
        foreach (var f in Fields)
            if (path.Contains((string)f["option"]!, StringComparison.Ordinal) && path.Contains("/" + (string)f["property"]!, StringComparison.Ordinal))
            {
                label = (string?)f["ui"]?["label"] ?? (string)f["label"]!;
                break;
            }
        lines.Add($"{label}: {a?.ToJsonString() ?? "(absent)"} → {b?.ToJsonString() ?? "(absent)"}");
    }
}

public sealed record RuleContext(string Id, string Name);
