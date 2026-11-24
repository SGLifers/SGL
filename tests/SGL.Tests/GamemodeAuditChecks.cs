using System.Text.Json.Nodes;
using SGL.Core;

static class GamemodeAuditChecks
{
    public static void Run(Action<bool, string> check)
    {
        check(GamemodeUi.Profiles.Count == 88 && (bool)GamemodeUi.Audit["inventoryComplete"]!, "Incomplete live preset audit");
        var fields = Configuration.Fields.ToDictionary(f => (string)f["id"]!);
        check(fields.Count == 180, "Expected 180 audited settings");
        var evidence = Json.Read(Modes.Resource("gamemode-setting-evidence.json"))["fields"]!.AsObject();
        check(evidence.Count == 60, "Missing new-setting hover/limit evidence");
        foreach (var item in evidence)
        {
            var f = fields.Values.Single(f => (string?)f["property"] == item.Key);
            check((string?)f["ui"]?["description"] == (string?)item.Value?["description"], "Hover description mismatch: " + item.Key);
            if (item.Value?["min"] != null)
                check(JsonNode.DeepEquals(f["ui"]?["min"], item.Value["min"]) && JsonNode.DeepEquals(f["ui"]?["max"], item.Value["max"]), "Slider evidence mismatch: " + item.Key);
        }
        foreach (var basis in BaseModes.All)
        {
            var p = Configuration.New(basis.Id);
            var profile = GamemodeUi.Profile(p)!;
            check(profile != null && (string?)profile["baseId"] == basis.Id, "Missing UI profile: " + basis.Name);
            var rules = Configuration.Rules(p);
            check(rules.Count == 2 && rules.Select(r => r.Id).Distinct().Count() == 2, "Missing rule phases: " + basis.Name);
            foreach (var rule in rules)
            {
                foreach (var tab in profile!["tabs"]![rule.Name]!.AsObject())
                    foreach (var id in tab.Value!.AsArray())
                    {
                        check(fields.ContainsKey((string)id!), "Unknown observed control");
                        var f = fields[(string)id!];
                        check((string?)f["ui"]?["category"] == tab.Key, "Wrong observed category: " + basis.Name + "/" + id);
                        check(GamemodeUi.InGame(p, f, rule.Name), "Observed control mislabeled extra");
                        check(Configuration.Value(p, f, rule.Id) != null, "Observed control has no mapping: " + basis.Name + "/" + id);
                    }
                var before = Configuration.Clone(p);
                var other = rules.Single(r => r.Id != rule.Id);
                string otherBefore = Json.Canonical(p["Options"]!["OptionMap"]![other.Id]);
                foreach (string id in new[] { "health", "damage", "roundLength", "botsCustomBotNum", "spawningRespawnTime" })
                {
                    var f = fields[id];
                    if (Configuration.Value(p, f, rule.Id) == null) continue;
                    string value = id == "roundLength" ? "601" : id == "botsCustomBotNum" ? "12" : "77";
                    Configuration.Set(p, f, value, rule.Id);
                    check((double)Configuration.Value(p, f, rule.Id)! == double.Parse(value), "Scoped edit failed");
                    check(Json.Canonical(p["Options"]!["OptionMap"]![other.Id]) == otherBefore, "Scoped edit changed other phase: " + basis.Name);
                    Configuration.Reset(p, before, f, rule.Id);
                    check(Json.Canonical(p) == Json.Canonical(before), "Scoped reset lost base values: " + basis.Name + "/" + id);
                }
                string[] loadout = [(string)Configuration.Catalog["weapons"]![0]!["id"]!, (string)Configuration.Catalog["weapons"]![1]!["id"]!, (string)Configuration.Catalog["equipment"]![0]!["id"]!];
                Configuration.SetLoadout(p, loadout, rule.Id);
                check(Configuration.Loadout(p, rule.Id)!.SequenceEqual(loadout), "Scoped loadout failed");
                check(Json.Canonical(p["Options"]!["OptionMap"]![other.Id]) == otherBefore, "Loadout changed other phase");
                p = before;
            }
        }
        var koth = Configuration.New("koth");
        check(GamemodeUi.InGame(koth, fields["modeTimeToCapture"], "Standard Rules"), "KOTH objective setting still missing");
        check(!GamemodeUi.InGame(koth, fields["modeKillScore"], "Standard Rules"), "KOTH incorrectly exposes kill scoring as a game control");
        check(!GamemodeUi.InGame(koth, fields["botDifficulty"], "Standard Rules"), "Hidden bot difficulty lost extra marker");
        var swat = Configuration.New("swat");
        check((double)Configuration.Value(swat, fields["precisionDamage"], Configuration.Rules(swat)[0].Id)! == 10000, "SWAT template was clamped by viewing its slider");
        var fire = Configuration.New("firecracker"); var phases = Configuration.Rules(fire);
        check((double)Configuration.Value(fire, fields["modeBombExplodeTime"], phases[0].Id)! == 50 && (double)Configuration.Value(fire, fields["modeBombExplodeTime"], phases[1].Id)! == 15, "Firecracker phase defaults collapsed");
        // A custom save may omit an inherited option. Editing and resetting it must not rewrite its parent.
        var timer = fields["roundLength"]; string option = (string)timer["option"]!;
        fire["Options"]!["OptionMap"]![phases[1].Id]!["Options"]!.AsObject().Remove(option);
        var inherited = Configuration.Clone(fire);
        Configuration.Set(fire, timer, "99", phases[1].Id);
        check((double)Configuration.Value(fire, timer, phases[1].Id)! == 99 && (double)Configuration.Value(fire, timer, phases[0].Id)! == 120, "Inherited phase edit changed parent");
        Configuration.Reset(fire, inherited, timer, phases[1].Id);
        check(Json.Canonical(fire) == Json.Canonical(inherited), "Reset failed to restore inherited field absence");
        var tdm = Configuration.New("tdm"); var standard = Configuration.Rules(tdm)[0];
        Configuration.Set(tdm, fields["infiniteReserves"], "true", standard.Id);
        check(Configuration.Value(tdm, fields["weaponDrops"], standard.Id)!.GetValue<bool>() == false, "Scoped reserves did not disable weapon drops");
    }
}
