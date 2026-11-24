// Adapted from the SpinGate installer; original mapping and transaction tests retained.
#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
namespace SGL.Core
{
    public static class Modes
    {
        public const string Faction = "/Script/PortalWarsCore.PortalWarsGameOptions_FactionSelection";
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16000000 };
        public static Dictionary<string, object> Obj(object value)
        {
            var result = value as Dictionary<string, object>;
            if (result == null)
                throw new InvalidDataException("Expected a JSON object.");
            return result;
        }
        public static Dictionary<string, object> Parse(string text)
        {
            return Obj(Json.DeserializeObject(text));
        }
        public static string Serialize(object value)
        {
            return Json.Serialize(value);
        }
        public static string Resource(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd();
        }
        static object Required(Dictionary<string, object> obj, string key)
        {
            if (!obj.ContainsKey(key))
                throw new InvalidDataException("Missing recipe field: " + key);
            return obj[key];
        }
        static double Number(object value)
        {
            if (!(value is int || value is long || value is decimal || value is double))
                throw new InvalidDataException("Expected a number.");
            return Convert.ToDouble(value);
        }
        static IEnumerable<Dictionary<string, object>> Fields()
        {
            return ((object[])Parse(Resource("settings-schema.json"))["fields"]).Select(Obj);
        }
        static object Default(Dictionary<string, object> field, object basis)
        {
            if (field.ContainsKey("baseDefaults") && Obj(field["baseDefaults"]).ContainsKey((string)basis))
                return Obj(field["baseDefaults"])[(string)basis];
            return field["default"];
        }
        static bool Same(object a, object b)
        {
            return a is bool || a is string ? Equals(a, b) : Number(a) == Number(b);
        }
        public static readonly Dictionary<string, string> VariantNames = CreateVariantNames();
        static Dictionary<string, string> CreateVariantNames()
        {
            var names = new Dictionary<string, string> {
            {"PortalWarsGameVariant:GV_KOTH_Team_Default", "King of the Hill"},
            {"PortalWarsGameVariant:GV_Deathmatch_Team_SWAT_Aeros", "SWAT"},
            {"PortalWarsGameVariant:GV_Deathmatch_Team_1round_Default", "Team Deathmatch"},
            {"PortalWarsGameVariant:GV_DOM_Team_1round_Default", "Domination"},
            {"PortalWarsGameVariant:GV_Deathmatch_Team_1v1", "1v1"},
            {"PortalWarsGameVariant:GV_Deathmatch_Team_BigHeadSnipers", "Big Head Snipers"},
            {"PortalWarsGameVariant:GV_Splitball_Team_Default", "Splitball"}
            };
            foreach (var basis in BaseModes.All)
                names.TryAdd(basis.Variant, basis.Name);
            return names;
        }
        static bool Compiled(Dictionary<string, object> recipe)
        {
            return recipe.ContainsKey("format") && Equals(recipe["format"], "spingate-payload");
        }
        static void ValidateName(object value)
        {
            var name = value as string;
            if (String.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(Char.IsControl))
                throw new InvalidDataException("Mode name must be 1-80 characters without control characters.");
        }
        static Dictionary<string, object> PayloadRecipe(Dictionary<string, object> payload, string name)
        {
            var recipe = new Dictionary<string, object> { { "format", "spingate-payload" }, { "version", 1 }, { "name", name }, { "payload", payload } };
            Validate(recipe);
            return recipe;
        }
        static void ValidatePayload(Dictionary<string, object> recipe)
        {
            if (Number(Required(recipe, "version")) != 1)
                throw new InvalidDataException("Unsupported payload recipe version.");
            ValidateName(Required(recipe, "name"));
            var payload = Obj(Required(recipe, "payload"));
            if (payload.Count != 3 || !payload.ContainsKey("Version") || !payload.ContainsKey("Variant") || !payload.ContainsKey("Options"))
                throw new InvalidDataException("Expected a game payload with Version, Variant and Options only.");
            if (Number(payload["Version"]) != 1 || !(payload["Variant"] is string) || !VariantNames.ContainsKey((string)payload["Variant"]))
                throw new InvalidDataException("Unsupported game payload variant or version.");
            var options = Obj(payload["Options"]);
            if (options.Count != 2 || !options.ContainsKey("CustomName") || !options.ContainsKey("OptionMap") || !Equals(options["CustomName"], recipe["name"]))
                throw new InvalidDataException("Payload name does not match its recipe.");
            var nodes = Obj(options["OptionMap"]);
            if (nodes.Count < 1 || nodes.Count > 32)
                throw new InvalidDataException("Unexpected game option contexts.");
            foreach (var node in nodes)
            {
                if (node.Key.Length != 32 || node.Key.Any(c => !Uri.IsHexDigit(c)))
                    throw new InvalidDataException("Invalid game option context ID.");
                var context = Obj(node.Value);
                if (!context.ContainsKey("Options") || context.Keys.Any(k => k != "Options" && k != "InheritsFrom"))
                    throw new InvalidDataException("Invalid option context.");
                if (context.ContainsKey("InheritsFrom") && (!(context["InheritsFrom"] is string) || !nodes.ContainsKey((string)context["InheritsFrom"])))
                    throw new InvalidDataException("Invalid inherited option context.");
                var settings = Obj(context["Options"]);
                if (settings.Count > 128 || settings.Keys.Any(k => !k.StartsWith("/Script/", StringComparison.Ordinal)))
                    throw new InvalidDataException("Invalid game option types.");
            }
            if (Encoding.UTF8.GetByteCount(Serialize(payload)) > 1048576)
                throw new InvalidDataException("One game payload exceeds 1 MB.");
        }
        public static void Validate(Dictionary<string, object> recipe)
        {
            if (Compiled(recipe))
            {
                ValidatePayload(recipe);
                return;
            }
            if (!Equals(Required(recipe, "format"), "spingate-mode") || Number(Required(recipe, "version")) != 1 ||
                !(Equals(Required(recipe, "base"), "koth-v1") || Equals(recipe["base"], "swat-v1") || Equals(recipe["base"], "tdm-v1")))
                throw new InvalidDataException("Unsupported SpinGate recipe version or base mode.");
            var name = Required(recipe, "name") as string;
            if (String.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(Char.IsControl))
                throw new InvalidDataException("Mode name must be 1–80 characters without control characters.");
            var catalog = Parse(Resource("catalog.json"));
            foreach (var slot in new[] { "weapon1", "weapon2", "equipment" })
            {
                string kind = slot == "equipment" ? "equipment" : "weapons";
                object selected = Required(recipe, slot);
                if (!((object[])catalog[kind]).Any(item => Equals(Obj(item)["id"], selected)))
                    throw new InvalidDataException("Unknown item in " + slot + ".");
            }
            var rules = Obj(Required(recipe, "rules"));
            var fields = Fields().ToList();
            if (rules.Keys.Any(key => !fields.Any(f => Equals(f["id"], key))))
                throw new InvalidDataException("Unsupported rule.");
            foreach (var field in fields)
            {
                string id = (string)field["id"], type = (string)field["type"];
                if (!rules.ContainsKey(id))
                    rules[id] = Default(field, recipe["base"]);
                if (type == "boolean")
                {
                    if (!(rules[id] is bool))
                        throw new InvalidDataException(id + " must be on or off.");
                }
                else if (type == "enum")
                {
                    if (!((object[])field["choices"]).Any(choice => Equals(Obj(choice)["value"], rules[id])))
                        throw new InvalidDataException("Invalid " + id + " selection.");
                }
                else
                {
                    double value = Number(rules[id]);
                    if (Double.IsNaN(value) || Double.IsInfinity(value) || value < Number(field["min"]) || value > Number(field["max"]) || (type == "integer" && value != Math.Floor(value)))
                        throw new InvalidDataException("Invalid " + id + ".");
                }
            }
            if ((bool)rules["infiniteReserves"])
                rules["weaponDrops"] = false;
        }
        public static Dictionary<string, object> ReadRecipe(string path)
        {
            if (new FileInfo(path).Length > 65536)
                throw new InvalidDataException("Recipe exceeds 64 KB.");
            var recipe = Parse(File.ReadAllText(path));
            Validate(recipe);
            return recipe;
        }
        public static List<Dictionary<string, object>> Recipes(object document)
        {
            var array = document as object[];
            if (array != null)
            {
                if (array.Length < 1 || array.Length > 100)
                    throw new InvalidDataException("A pack needs 1-100 modes.");
                var compiled = array.Select(value =>
                {
                    var item = Obj(value);
                    return PayloadRecipe(Obj(Required(item, "payload")), Required(item, "name") as string);
                }).ToList();
                ValidateBatch(compiled);
                return compiled;
            }
            var doc = Obj(document);
            if (doc.ContainsKey("Version") && doc.ContainsKey("Variant") && doc.ContainsKey("Options"))
                return new List<Dictionary<string, object>> { PayloadRecipe(doc, Required(Obj(doc["Options"]), "CustomName") as string) };
            if (doc.ContainsKey("payload") && doc.ContainsKey("name") && !doc.ContainsKey("format"))
                return new List<Dictionary<string, object>> { PayloadRecipe(Obj(doc["payload"]), doc["name"] as string) };
            string format = Required(doc, "format") as string;
            if (format == "spingate-mode" || format == "spingate-payload")
            {
                Validate(doc);
                return new List<Dictionary<string, object>> { doc };
            }
            if ((format != "spingate-pack" && format != "spingate-payload-pack") || Number(Required(doc, "version")) != 1)
                throw new InvalidDataException("Unsupported mode pack.");
            var values = Required(doc, "modes") as object[];
            if (values == null || values.Length < 1 || values.Length > 100)
                throw new InvalidDataException("A pack needs 1-100 modes.");
            if (format == "spingate-payload-pack")
                return Recipes(values);
            var recipes = values.Select(Obj).ToList();
            ValidateBatch(recipes);
            return recipes;
        }
        public static List<Dictionary<string, object>> ReadRecipes(string path)
        {
            if (new FileInfo(path).Length > 16000000)
                throw new InvalidDataException("Mode pack exceeds 16 MB.");
            return Recipes(Json.DeserializeObject(File.ReadAllText(path)));
        }
        public static void ValidateBatch(List<Dictionary<string, object>> recipes)
        {
            if (recipes == null || recipes.Count < 1 || recipes.Count > 100)
                throw new InvalidDataException("A pack needs 1–100 recipes.");
            foreach (var recipe in recipes)
                Validate(recipe);
            if (recipes.Select(r => ((string)r["name"]).Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != recipes.Count)
                throw new InvalidDataException("Mode names in a pack must be unique.");
        }
        public static List<Dictionary<string, object>> Embedded(string path)
        {
            byte[] marker = Encoding.ASCII.GetBytes("SPINGATEPACK1");
            using (var stream = File.OpenRead(path))
            {
                if (stream.Length < marker.Length + 4)
                    return null;
                stream.Seek(-marker.Length, SeekOrigin.End);
                var actual = new byte[marker.Length];
                stream.ReadExactly(actual);
                if (!marker.SequenceEqual(actual))
                    return null;
                stream.Seek(-marker.Length - 4, SeekOrigin.End);
                var bytes = new byte[4];
                stream.ReadExactly(bytes);
                int length = BitConverter.ToInt32(bytes, 0);
                if (length < 1 || length > 16000000 || length > stream.Length - marker.Length - 4)
                    throw new InvalidDataException("Invalid embedded pack length.");
                stream.Seek(-marker.Length - 4 - length, SeekOrigin.End);
                using (var reader = new BinaryReader(stream))
                    return Recipes(Json.DeserializeObject(new UTF8Encoding(false, true).GetString(reader.ReadBytes(length))));
            }
        }
        static string Values(Dictionary<string, object> payload, string suffix, string property)
        {
            var values = new List<string>();
            foreach (var node in Obj(Obj(payload["Options"])["OptionMap"]).Values)
                foreach (var option in Obj(Obj(node)["Options"]))
                {
                    if (!option.Key.EndsWith(suffix, StringComparison.Ordinal))
                        continue;
                    var setting = Obj(option.Value);
                    if (!setting.ContainsKey(property))
                        continue;
                    object value = setting[property];
                    if (value is Dictionary<string, object> && Obj(value).ContainsKey("defaultValue"))
                        value = Obj(value)["defaultValue"];
                    values.Add(value is bool ? ((bool)value ? "On" : "Off") : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                }
            return String.Join(" / ", values.Distinct());
        }
        static string PayloadSummary(Dictionary<string, object> recipe)
        {
            var p = Obj(recipe["payload"]);
            var names = new List<string>();
            var catalog = Parse(Resource("catalog.json"));
            foreach (var node in Obj(Obj(p["Options"])["OptionMap"]).Values)
            {
                var settings = Obj(Obj(node)["Options"]);
                if (!settings.ContainsKey(Faction))
                    continue;
                var loadouts = Obj(Obj(Obj(settings[Faction])["CustomLoadouts"])["customLoadouts"]);
                if (!loadouts.ContainsKey("None"))
                    continue;
                var choices = Obj(loadouts["None"])["customLoadouts"] as object[];
                if (choices == null || choices.Length == 0)
                    continue;
                var items = Obj(Obj(choices[0])["selectedItems"]);
                foreach (var slot in new[] { "LoadoutSlot.CustomWeapon.01.Weapon", "LoadoutSlot.CustomWeapon.02.Weapon", "LoadoutSlot.CustomEquipment" })
                {
                    if (!items.ContainsKey(slot))
                        continue;
                    string id = Convert.ToString(items[slot]).Replace("LoadoutItem:", "");
                    var pool = (object[])catalog[slot.EndsWith("CustomEquipment") ? "equipment" : "weapons"];
                    var found = pool.Select(Obj).FirstOrDefault(i => Equals(i["id"], id));
                    names.Add(found == null ? id : (string)found["name"]);
                }
                break;
            }
            var lines = new List<string> { (string)recipe["name"], VariantNames[(string)p["Variant"]], names.Count == 0 ? "Base mode loadout" : String.Join(" / ", names) };
            string[][] fields = {
                new[]{"Health","GameOptions_Damage","DefaultMaxHealth"}, new[]{"Damage multiplier","DamageCustomModifiers","DamageMultiplier"},
                new[]{"Melee multiplier","DamageCustomModifiers","MeleeDamageMultiplier"}, new[]{"Precision multiplier","DamageCustomModifiers","PrecisionDamageMultiplier"},
                new[]{"Friendly fire","GameOptions_Damage","AllowFriendlyFire"}, new[]{"Equipment cooldown multiplier","AbilityCustomModifiers","EquipmentCooldownMultiplier"},
                new[]{"Infinite reserves","GameOptions_Weapons","InfiniteReserves"}, new[]{"Weapon drops","WeaponPickups","bEnabled"},
                new[]{"Map pickups","ItemPads","bEnabled"}, new[]{"Bots","BotSpawning","bEnabled"}, new[]{"Bot difficulty","BotSpawning","Difficulty"},
                new[]{"Ranged ammo trigger","GameOptions_Weapons","AmmoGrantRanged"}, new[]{"Ranged ammo amount","GameOptions_Weapons","AmmoGrantAmountRanged"},
                new[]{"Melee ammo trigger","GameOptions_Weapons","AmmoGrantMelee"}, new[]{"Melee ammo amount","GameOptions_Weapons","AmmoGrantAmountMelee"},
                new[]{"Round score limit","GameOptions_ScoreLimit","ScoreLimit"}, new[]{"Round seconds","GameOptions_RoundTimer","RoundLength"}
            };
            foreach (var f in fields)
            {
                string value = Values(p, f[1], f[2]);
                if (value.Length > 0)
                    lines.Add(f[0] + ": " + value);
            }
            lines.Add("Custom settings require in-game testing. Easy/Hard bot tags, if present, are experimental.");
            return String.Join("\r\n", lines);
        }
        public static string Summary(Dictionary<string, object> recipe)
        {
            Validate(recipe);
            if (Compiled(recipe))
                return PayloadSummary(recipe);
            var catalog = Parse(Resource("catalog.json"));
            var names = new List<string>();
            foreach (var slot in new[] { "weapon1", "weapon2", "equipment" })
            {
                string kind = slot == "equipment" ? "equipment" : "weapons";
                names.Add((string)Obj(((object[])catalog[kind]).First(item => Equals(Obj(item)["id"], recipe[slot])))["name"]);
            }
            var rules = Obj(recipe["rules"]);
            return recipe["name"] + "\r\n" + (Equals(recipe["base"], "swat-v1") ? "SWAT" : Equals(recipe["base"], "tdm-v1") ? "Team Deathmatch" : "King of the Hill") + "\r\n" + String.Join(" + ", names) + "\r\n" + rules["health"] + " health · " + rules["damage"] + "× damage · " + ((bool)rules["infiniteReserves"] ? "Infinite reserves" : "Standard ammo")
                + "\r\nWeapon drops: " + ((bool)rules["weaponDrops"] ? "on" : "off") + " · Map pickups (all item pads): " + ((bool)rules["mapPickups"] ? "on" : "off")
                + "\r\nFriendly fire: " + ((bool)rules["friendlyFire"] ? "on" : "off") + " · Big heads: " + ((bool)rules["bigHeads"] ? "on" : "off")
                + "\r\nMelee damage: " + rules["meleeDamage"] + "x · Precision damage: " + rules["precisionDamage"] + "x"
                + "\r\nEquipment cooldown: " + rules["equipmentCooldown"] + "x"
                + "\r\nChanged rules: " + String.Join("; ", Fields().Where(f => !Same(rules[(string)f["id"]], Default(f, recipe["base"]))).Select(f => f["label"] + ": " + rules[(string)f["id"]]));
        }
        static byte[] Archive(object payload)
        {
            using (var stream = new MemoryStream())
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                {
                    var entry = zip.CreateEntry("payload.json", CompressionLevel.Optimal);
                    using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                        writer.Write(Serialize(payload));
                }
                return new byte[] { 1 }.Concat(stream.ToArray()).ToArray();
            }
        }
        public static byte[] Pack(Dictionary<string, object> recipe)
        {
            Validate(recipe);
            if (Compiled(recipe))
                return Archive(recipe["payload"]);
            var payload = Parse(Resource(Equals(recipe["base"], "swat-v1") ? "swat.json" : Equals(recipe["base"], "tdm-v1") ? "tdm.json" : "koth.json"));
            var options = Obj(payload["Options"]);
            options["CustomName"] = recipe["name"];
            var rules = Obj(recipe["rules"]);
            int count = 0;
            foreach (var node in Obj(options["OptionMap"]).Values)
            {
                var settings = Obj(Obj(node)["Options"]);
                if (settings.ContainsKey(Faction))
                {
                    var faction = Obj(settings[Faction]);
                    faction["bAllowLoadoutChangesDuringGameplay"] = false;
                    faction["CustomLoadouts"] = new
                    {
                        customLoadouts = new Dictionary<string, object> {
                        { "None", new { customLoadouts = new[] { new {
                            selectedItems = new Dictionary<string, object> {
                                {"LoadoutSlot.CustomWeapon.01.Weapon", "LoadoutItem:" + recipe["weapon1"]},
                                {"LoadoutSlot.CustomWeapon.02.Weapon", "LoadoutItem:" + recipe["weapon2"]},
                                {"LoadoutSlot.CustomEquipment", "LoadoutItem:" + recipe["equipment"]}
                            }, randomItemTags = new Dictionary<string, object>(), randomWeaponAttachmentSlots = new Dictionary<string, object>()
                        } } } }
                    }
                    };
                    count++;
                }
                foreach (var field in Fields())
                {
                    string id = (string)field["id"], option = (string)field["option"], property = (string)field["property"];
                    if (!(bool)field["applyDefault"] && Same(rules[id], Default(field, recipe["base"])))
                        continue;
                    if (!settings.ContainsKey(option))
                        continue;
                    var target = Obj(settings[option]);
                    if (!target.ContainsKey(property))
                        throw new InvalidDataException("Missing template mapping for " + id);
                    if ((bool)field["wrapped"])
                        Obj(target[property])["defaultValue"] = rules[id];
                    else
                        target[property] = rules[id];
                }
            }
            if (count != 6)
                throw new InvalidDataException("Unexpected base template structure.");
            using (var stream = new MemoryStream())
            {
                // Create the ZIP separately so offsets match the game's existing prefixed archives.
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                {
                    var entry = zip.CreateEntry("payload.json", CompressionLevel.Optimal);
                    using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                        writer.Write(Serialize(payload));
                }
                return new byte[] { 1 }.Concat(stream.ToArray()).ToArray();
            }
        }
        public static bool GameRunning()
        {
            return Process.GetProcessesByName("PortalWars2-Win64-Shipping").Length > 0;
        }
        public static string Install(Dictionary<string, object> recipe, string directory, Func<bool> gameRunning)
        {
            return InstallBatch(new List<Dictionary<string, object>> { recipe }, directory, gameRunning);
        }
        public enum ExistingModeAction
        {
            Reject, Skip, Update
        }
        public sealed class InstallResult
        {
            public int InstalledCount, UpdatedCount, SkippedCount;
            public string BackupPath, Report;
        }
        static bool IsMode(Dictionary<string, object> file)
        {
            return file.ContainsKey("ContentType") && Equals(file["ContentType"], "CustomModes");
        }
        static string ModeName(Dictionary<string, object> file)
        {
            return file.ContainsKey("FileName") ? Convert.ToString(file["FileName"]).Trim() : "";
        }
        static string RecipientOwner(List<Dictionary<string, object>> files)
        {
            var local = files.Where(IsMode).Where(f => f.ContainsKey("OwnerId") && !String.IsNullOrWhiteSpace(f["OwnerId"] as string)).ToList();
            var owners = local.Select(f => (string)f["OwnerId"]).Distinct(StringComparer.Ordinal).ToArray();
            if (owners.Length == 1)
                return owners[0];
            var probe = local.Where(f => ModeName(f) == "SpinGate Owner Check").ToList();
            if (probe.Count == 1)
                return (string)probe[0]["OwnerId"];
            throw new InvalidDataException(owners.Length == 0
                ? "Save a custom mode in-game on this PC first, then close the game and retry. Imported maps cannot identify your save owner."
                : "Multiple custom-mode owners found. Save a new mode named SpinGate Owner Check in-game, close the game, and retry.");
        }
        static string Inside(string root, string relative)
        {
            if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
                throw new InvalidDataException("Invalid relative save path.");
            string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Save path escapes CloudSave.");
            return path;
        }
        public static string InstallBatch(List<Dictionary<string, object>> recipes, string directory, Func<bool> gameRunning)
        {
            return InstallModes(recipes, directory, gameRunning, ExistingModeAction.Reject).Report;
        }
        public static InstallResult InstallModes(List<Dictionary<string, object>> recipes, string directory, Func<bool> gameRunning, ExistingModeAction action)
        {
            ValidateBatch(recipes);
            if (gameRunning())
                throw new InvalidOperationException("Close Splitgate before installing modes.");
            string root = Path.GetFullPath(directory), manifestPath = Path.Combine(root, "CloudSaveManifest.json");
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("Save index not found. Save a custom mode in-game first, or choose the correct CloudSave folder.");
            byte[] original = File.ReadAllBytes(manifestPath);
            Encoding encoding;
            if (original.Length >= 2 && original[0] == 255 && original[1] == 254)
                encoding = Encoding.Unicode;
            else if (original.Length >= 2 && original[0] == 254 && original[1] == 255)
                encoding = Encoding.BigEndianUnicode;
            else
                encoding = new UTF8Encoding(original.Length >= 3 && original[0] == 239 && original[1] == 187 && original[2] == 191);
            Dictionary<string, object> manifest;
            using (var reader = new StreamReader(new MemoryStream(original), encoding, true))
                manifest = Parse(reader.ReadToEnd());
            var array = Required(manifest, "Files") as object[];
            if (array == null)
                throw new InvalidDataException("The save index Files value must be an array.");
            var all = array.Select(Obj).ToList();
            var existing = new HashSet<string>(all.Where(IsMode).Select(ModeName), StringComparer.OrdinalIgnoreCase);
            var conflicts = recipes.Where(r => existing.Contains(((string)r["name"]).Trim())).Select(r => (string)r["name"]).ToArray();
            if (action == ExistingModeAction.Reject && conflicts.Length > 0)
                throw new InvalidOperationException("Existing mode name(s): " + String.Join(", ", conflicts) + ". Nothing was installed.");
            var selected = recipes.Where(r => action != ExistingModeAction.Skip || !existing.Contains(((string)r["name"]).Trim())).ToList();
            var result = new InstallResult { SkippedCount = action == ExistingModeAction.Skip ? conflicts.Length : 0, UpdatedCount = action == ExistingModeAction.Update ? conflicts.Length : 0, InstalledCount = selected.Count };
            if (selected.Count == 0)
            {
                result.Report = "All " + result.SkippedCount + " selected mode names already exist. No files changed; no backup needed.";
                return result;
            }
            string owner = RecipientOwner(all);
            var requested = new HashSet<string>(selected.Select(r => ((string)r["name"]).Trim()), StringComparer.OrdinalIgnoreCase);
            var replaced = action == ExistingModeAction.Update ? all.Where(f => IsMode(f) && requested.Contains(ModeName(f))).ToList() : new List<Dictionary<string, object>>();
            var files = all.Where(f => !replaced.Contains(f)).ToList();
            string id = Guid.NewGuid().ToString(), timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var outputs = new Dictionary<string, byte[]>();
            foreach (var recipe in selected)
            {
                string fileId = Guid.NewGuid().ToString(), saveId = Guid.NewGuid().ToString();
                string relative = "CustomModes/" + fileId + "/" + saveId + ".bin";
                outputs.Add(relative, Pack(recipe));
                files.Add(new Dictionary<string, object> {
                    {"FileId",fileId},{"FileName",recipe["name"]},{"ContentType","CustomModes"},{"OwnerId",owner},{"AuthorDisplayName",""},{"CreatedAt",timestamp},
                    {"Saves",new[]{new{SaveId=saveId,RelativePath=relative,SaveType="Manual",CreatedAt=timestamp}}}
                });
            }
            manifest["Files"] = files;
            string backup = Path.Combine(root, "Backup", "SG-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + id.Substring(0, 8));
            Directory.CreateDirectory(backup);
            result.BackupPath = backup;
            File.WriteAllBytes(Path.Combine(backup, "CloudSaveManifest.json"), original);
            foreach (var old in replaced)
            {
                var saves = old.ContainsKey("Saves") ? old["Saves"] as object[] : null;
                if (saves == null)
                    continue;
                foreach (var value in saves)
                {
                    var save = Obj(value);
                    if (!save.ContainsKey("RelativePath"))
                        continue;
                    string relative = save["RelativePath"] as string;
                    string path = Inside(root, relative);
                    if (!File.Exists(path))
                        continue;
                    string saved = Inside(Path.Combine(backup, "PreviousFiles"), relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(saved));
                    File.Copy(path, saved, false);
                }
            }
            File.WriteAllText(Path.Combine(backup, "installation.json"), Serialize(new
            {
                modeNames = selected.Select(r => r["name"]).ToArray(),
                modeFiles = outputs.Keys.ToArray(),
                installedAt = timestamp,
                skippedModeNames = action == ExistingModeAction.Skip ? conflicts : new string[0],
                replacedModeNames = replaced.Select(ModeName).ToArray(),
                installerVersion = "2.0"
            }), new UTF8Encoding(false));
            string temp = Path.Combine(root, "CloudSaveManifest." + id + ".tmp");
            var created = new List<string>();
            bool committed = false;
            try
            {
                foreach (var output in outputs)
                {
                    string destination = Inside(root, output.Key), folder = Path.GetDirectoryName(destination);
                    if (Directory.Exists(folder))
                        throw new IOException("Generated mode ID already exists. Retry.");
                    Directory.CreateDirectory(folder);
                    created.Add(folder);
                    File.WriteAllBytes(destination, output.Value);
                }
                File.WriteAllText(temp, Serialize(manifest), encoding);
                if (gameRunning())
                    throw new InvalidOperationException("Splitgate started during installation. Close it and retry.");
                using (var check = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    var current = new byte[check.Length];
                    int count = 0;
                    while (count < current.Length)
                    {
                        int read = check.Read(current, count, current.Length - count);
                        if (read == 0)
                            break;
                        count += read;
                    }
                    if (count != current.Length || !original.SequenceEqual(current))
                        throw new IOException("Save index changed during installation. No modes were registered.");
                    File.Replace(temp, manifestPath, Path.Combine(backup, "CloudSaveManifest.before-replace.json"));
                    committed = true;
                }
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
                if (!committed)
                    foreach (string folder in created)
                    {
                        foreach (string file in Directory.GetFiles(folder))
                            File.Delete(file);
                        Directory.Delete(folder);
                    }
            }
            result.Report = "Installed " + selected.Count + " mode(s): " + String.Join(", ", selected.Select(r => (string)r["name"])) + ".\r\nUpdated: " + result.UpdatedCount + ". Skipped: " + result.SkippedCount + ".\r\nStart Splitgate and choose the modes in your custom lobby.\r\n\r\nBackup: " + backup;
            return result;
        }
    }

}
