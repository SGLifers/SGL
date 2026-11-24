using System.Text.Json.Nodes;

namespace SGL.Core;

/// <summary>Controls observed in every View All preset, for each advanced rule phase.</summary>
public static class GamemodeUi
{
    public static readonly JsonObject Audit = Json.Read(Modes.Resource("gamemode-ui-audit.json")).AsObject();
    public static IReadOnlyList<JsonObject> Profiles { get; } = Audit["modes"]!.AsArray().Cast<JsonObject>().ToArray();
    public static JsonObject? Profile(JsonObject payload) => Profiles.SingleOrDefault(p => (string?)p["variant"] == (string?)payload["Variant"]);
    public static bool InGame(JsonObject payload, JsonObject field, string phase) =>
        Profile(payload)?["tabs"]?[phase]?.AsObject().Any(tab => tab.Value!.AsArray().Any(id => (string?)id == (string?)field["id"])) == true;
    public static IEnumerable<JsonObject> Fields(JsonObject payload, RuleContext rule) => Configuration.Fields
        .Where(f => InGame(payload, f, rule.Name) || Configuration.Value(payload, f, rule.Id) != null);
}
