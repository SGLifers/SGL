using System.Text.Json.Nodes;
using SGL.Core;

static class BaseModeChecks
{
    public static void Run(Action<bool, string> check)
    {
        string[] added = ["takedown-og", "firecracker", "domination-1-round", "graffiti", "gun-game",
            "hotzone-1-round", "splitball-1-round", "teabag-confirmed", "arena-royale", "big-head-snipers",
            "chaos-domination", "chaos-firecracker", "chaos-splitball", "ffa-one-in-the-chamber",
            "ffa-one-in-the-chamber-goliath", "ffa-fiesta", "flying-splitball", "ffa-deathmatch",
            "multi-team-deathmatch", "multi-team-deathmatch-2v2v2v2", "multi-team-deathmatch-3v3v3",
            "multi-team-deathmatch-4v4v4v4v4v4", "multi-team-deathmatch-6v6v6v6", "race",
            "showdown-firecracker", "onslight-graffiti", "speed-bats", "team-shotty-snipers-aeros",
            "team-shotty-snipers-powers", "tdm-1v1"];
        check(BaseModes.All.Count == 88, "Expected the complete catalog of View All base templates");
        check(BaseModes.All.Select(b => b.Id).Distinct().Count() == 88, "Duplicate template IDs");
        check(BaseModes.All.Select(b => b.Name).SequenceEqual(BaseModes.All.Select(b => b.Name).Order(StringComparer.OrdinalIgnoreCase)), "Base modes are not alphabetical");
        check(added.All(id => BaseModes.All.Any(b => b.Id == id)), "A requested base is missing");
        foreach (var basis in BaseModes.All)
        {
            using var stream = typeof(BaseModes).Assembly.GetManifestResourceStream(basis.Resource)!;
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            check(Json.Hash(bytes.ToArray()).Equals(basis.Sha256, StringComparison.OrdinalIgnoreCase), "Source template changed: " + basis.Id);
            var p = Configuration.New(basis.Id);
            string original = Json.Canonical(p);
            check((string?)p["Variant"] == basis.Variant && Configuration.Supported(p), "Unrecognized base variant: " + basis.Id);
            var recipe = Configuration.Wrapped(p);
            check(Modes.Summary(recipe).Contains(Configuration.Name(p)), "Base summary failed: " + basis.Id);
            check(Json.Canonical(Configuration.ReadArchive(Modes.Pack(recipe))) == original, "Archive round trip changed base rules: " + basis.Id);
            check(Json.Canonical(p) == original, "Validation mutated the base: " + basis.Id);
            var renamed = Configuration.New(basis.Id);
            renamed["Options"]!["CustomName"] = "My " + basis.Name;
            check(Json.Canonical(renamed["Options"]!["OptionMap"]) == Json.Canonical(p["Options"]!["OptionMap"]), "Naming a mode changed objective rules");
            check(Json.Canonical(Configuration.New(basis.Id)) == original, "Template edits leaked into another new mode");
            foreach (string setting in new[] { "precisionDamage", "modeScoreLimit" })
            {
                var field = Configuration.Fields.Single(f => (string?)f["id"] == setting);
                if (Configuration.Value(p, field) == null) continue;
                Configuration.Set(p, field, setting == "precisionDamage" ? "7.5" : "123");
                var expected = Configuration.New(basis.Id);
                foreach (var context in Configuration.Contexts(expected))
                    if (context[(string)field["option"]!] is JsonObject options && options[(string)field["property"]!] is JsonNode value)
                    {
                        JsonNode changed = JsonValue.Create(setting == "precisionDamage" ? 7.5 : 123)!;
                        if ((bool)field["wrapped"]!) value["defaultValue"] = changed;
                        else options[(string)field["property"]!] = changed;
                    }
                check(Json.Canonical(p) == Json.Canonical(expected), "Setting edit changed unrelated objective/team rules: " + basis.Id);
                Configuration.Reset(p, Configuration.New(basis.Id), field);
                check(Json.Canonical(p) == original, "Setting reset lost base rules: " + basis.Id);
            }
            string[] loadout = new string[3];
            for (int i = 0; i < 3; i++) loadout[i] = (string)Configuration.Catalog[i == 2 ? "equipment" : "weapons"]![0]!["id"]!;
            Configuration.SetLoadout(p, loadout);
            check(Configuration.Loadout(p)!.SequenceEqual(loadout), "Custom loadout failed: " + basis.Id);
            var originalContexts = Configuration.New(basis.Id)["Options"]!["OptionMap"]!.AsObject();
            foreach (var context in p["Options"]!["OptionMap"]!.AsObject())
                if (context.Value!["Options"]![Modes.Faction] is JsonObject faction)
                {
                    var before = originalContexts[context.Key]!["Options"]![Modes.Faction]!;
                    faction["CustomLoadouts"] = before["CustomLoadouts"]!.DeepClone();
                    faction["bAllowLoadoutChangesDuringGameplay"] = before["bAllowLoadoutChangesDuringGameplay"]!.DeepClone();
                }
            check(Json.Canonical(p) == original, "Loadout edit changed other base options: " + basis.Id);
        }
        check(BaseModes.Find("tdm-1v1").Variant == "PortalWarsGameVariant:GV_Deathmatch_Team_1v1", "1v1 used standard TDM");
        check(BaseModes.Find("splitball-1-round").Variant == "PortalWarsGameVariant:GV_Splitball_Team_1round_Default", "One-round Splitball used the legacy variant");
        check(Configuration.New("race")["Options"]!["OptionMap"]!.AsObject().Count == 3, "Race contexts were expanded from another mode");
    }
}
