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
    public static JsonObject New(string basis) => (JsonObject)Json.Read(Modes.Resource(basis + ".json"));
    public static IEnumerable<JsonObject> Contexts(JsonObject payload) => payload["Options"]!["OptionMap"]!.AsObject().Select(n => n.Value!["Options"]!.AsObject());
    public static JsonNode? Value(JsonObject payload, JsonObject field)
    {
        string option = (string)field["option"]!, property = (string)field["property"]!;
        foreach (var options in Contexts(payload))
            if (options[option]?[property] is JsonNode value)
                return (bool)field["wrapped"]! ? value["defaultValue"]?.DeepClone() : value.DeepClone();
        return null;
    }
    public static void Set(JsonObject payload, JsonObject field, string text)
    {
        if ((string)field["id"]! == "weaponDrops" && text.Equals("true", StringComparison.OrdinalIgnoreCase) && Value(payload, Fields.Single(f => (string)f["id"]! == "infiniteReserves"))?.GetValue<bool>() == true)
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
        int count = 0;
        foreach (var options in Contexts(payload))
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
            Set(payload, Fields.Single(f => (string)f["id"]! == "weaponDrops"), "false");
    }
    public static void Reset(JsonObject payload, JsonObject opened, JsonObject field)
    {
        string id = (string)field["id"]!, option = (string)field["option"]!, property = (string)field["property"]!;
        if (id == "weaponDrops" && Value(opened, field)?.GetValue<bool>() == true && Value(payload, Fields.Single(f => (string)f["id"]! == "infiniteReserves"))?.GetValue<bool>() == true)
            throw new InvalidDataException("Weapon drops must stay off while infinite reserves are enabled.");
        foreach (var context in payload["Options"]!["OptionMap"]!.AsObject())
            if (context.Value?["Options"]?[option] is JsonObject settings && opened["Options"]?["OptionMap"]?[context.Key]?["Options"]?[option]?[property] is JsonNode original)
                settings[property] = original.DeepClone();
        if (id == "infiniteReserves" && Value(payload, field)?.GetValue<bool>() == true)
            Set(payload, Fields.Single(f => (string)f["id"]! == "weaponDrops"), "false");
    }
    public static string[]? Loadout(JsonObject p)
    {
        foreach (var options in Contexts(p))
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
    public static void SetLoadout(JsonObject p, string[] ids)
    {
        for (int i = 0; i < 3; i++)
            if (!Catalog[i == 2 ? "equipment" : "weapons"]!.AsArray().Any(n => (string)n!["id"]! == ids[i]))
                throw new InvalidDataException("Unknown loadout item.");
        foreach (var options in Contexts(p))
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
