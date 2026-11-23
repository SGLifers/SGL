using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SGL.Core;

public static class SettingSearch
{
    static bool MatchesText(string query, IEnumerable<string?> parts)
    {
        string index = string.Join(" ", parts);
        return Regex.Matches(query, @"[\p{L}\p{Nd}]+")
            .Select(m => m.Value).All(term => index.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
    public static bool Matches(JsonObject field, string query) => MatchesText(query,
        new[] { (string?)field["label"], (string?)field["id"], (string?)field["property"], (string?)field["help"],
            (string?)field["ui"]?["label"], (string?)field["ui"]?["category"], (string?)field["ui"]?["description"] }
        .Concat(field["choices"]?.AsArray().SelectMany(n => new[] { (string?)n?["label"], (string?)n?["value"] }) ?? []));
    public static bool LoadoutMatches(string query) => MatchesText(query,
        new[] { "Loadouts", "Primary weapon", "Secondary weapon", "Equipment", "Custom starting loadout" }
        .Concat(new[] { "weapons", "equipment" }.SelectMany(kind => Configuration.Catalog[kind]!.AsArray().Select(n => (string?)n?["name"]))));
}
