using System.Text.Json.Nodes;
using System.Reflection;
namespace SGL.Core;

public sealed record BaseMode(string Id, string Name, string Variant, string Resource, string Sha256);

public static class BaseModes
{
    public static IReadOnlyList<BaseMode> All { get; } = Array.AsReadOnly(
        ReadResource("base-modes.json").AsArray().Select(n => new BaseMode(
            (string)n!["id"]!, (string)n["name"]!, (string)n["variant"]!,
            (string)n["resource"]!, (string)n["sha256"]!))
            .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Id, StringComparer.Ordinal).ToArray());

    public static BaseMode Find(string id) => All.SingleOrDefault(b => b.Id == id)
        ?? throw new ArgumentException("Unknown base gamemode: " + id, nameof(id));

    public static JsonObject Create(string id) => (JsonObject)ReadResource(Find(id).Resource);

    static JsonNode ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidDataException("Missing base gamemode resource: " + name);
        using var reader = new StreamReader(stream);
        return Json.Read(reader.ReadToEnd());
    }
}
