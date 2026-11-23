using System.Text;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.IO.Compression;
using SGL.Core;

int assertions = 0;
void Check(bool test, string message) { assertions++; if (!test) throw new Exception(message); }
void Reject(Action action, string message) { bool failed = false; try { action(); } catch { failed = true; } Check(failed, message); }
string work = Path.Combine(Path.GetTempPath(), "sgl-test-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);
Updates.EnsureWindowsExecutable(Environment.ProcessPath!);
Check(true, "Valid Windows x64 host accepted");
Check(Json.Canonical(Json.Read("{\"n\":1.00}")) == Json.Canonical(Json.Read("{\"n\":1e0}")), "Equivalent numbers created a false diff");
Check(Json.Canonical(Json.Read("{\"n\":0.00000100}")) == Json.Canonical(Json.Read("{\"n\":1e-6}")), "Small number normalization differs");
string cloud = Path.Combine(work, "CloudSave");
Directory.CreateDirectory(cloud);
string id = Guid.NewGuid().ToString(), sid = Guid.NewGuid().ToString(), relative = $"CustomModes/{id}/{sid}.bin";
var payload = Configuration.New("tdm");
payload["Options"]!["CustomName"] = "Original mode";
var source = Configuration.Clone(payload);
var firstContext = Configuration.Contexts(payload).First();
firstContext["/Script/SGL.TestUnknown"] = new JsonObject { ["keep"] = "exactly" };
string archive = GameStore.Inside(cloud, relative);
Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
File.WriteAllBytes(archive, Modes.Pack(Configuration.Wrapped(payload)));
var manifest = new JsonObject { ["Extra"] = "preserve", ["Files"] = new JsonArray(new JsonObject { ["FileId"] = id, ["FileName"] = "Original mode", ["ContentType"] = "CustomModes", ["OwnerId"] = "recipient", ["Saves"] = new JsonArray(new JsonObject { ["SaveId"] = sid, ["RelativePath"] = relative, ["CreatedAt"] = "2026-01-01T00:00:00Z" }) }) };
string index = Path.Combine(cloud, "CloudSaveManifest.json");
File.WriteAllText(index, manifest.ToJsonString(), Encoding.Unicode);
var store = new GameStore(cloud, () => false);
var initial = store.Read();
Check(initial.Modes.Count == 1, "Archive scan failed");
using (var history = new History(Path.Combine(work, "History")))
{
    string first = history.Capture(initial, "Initial baseline")!;
    Check(first != null, "No initial commit");
    Check(history.Capture(initial, "No-op") == null, "No-op created history");
    var reversed = (JsonObject)Json.Read(Json.Canonical(payload));
    Check(Json.Canonical(payload) == Json.Canonical(reversed), "Canonical payload unstable");
    var field = Configuration.Fields.Single(f => (string)f["id"]! == "precisionDamage");
    Configuration.Set(payload, field, "10");
    payload["Options"]!["CustomName"] = "Renamed mode";
    var before = File.ReadAllBytes(index);
    string backup = store.Apply(initial, id, payload);
    Check(File.ReadAllBytes(Path.Combine(backup, "CloudSaveManifest.json")).SequenceEqual(before), "Backup not byte-exact");
    var changed = store.Read();
    Check(changed.Modes[0].Id == id, "Rename lost mode identity");
    Check((string?)changed.Manifest["Extra"] == "preserve", "Unrelated index metadata lost");
    Check((string?)Configuration.Contexts(changed.Modes[0].Payload).First()["/Script/SGL.TestUnknown"]?["keep"] == "exactly", "Unknown options lost");
    Check((double)Configuration.Value(changed.Modes[0].Payload, field)! == 10, "Expanded precision lost");
    Check(File.ReadAllBytes(index)[0] == 255 && File.ReadAllBytes(index)[1] == 254, "Manifest encoding changed");
    Check(File.Exists(archive), "Old archive removed");
    string second = history.Capture(changed, "SGL save")!;
    Check(history.Revisions().Length == 2, "History count mismatch");
    Check(history.Compare(first!, second).Contains("Precision Damage Multiplier"), "Semantic diff missing label");
    Check(history.Mode(first!, id)["Options"]!["CustomName"]!.GetValue<string>() == "Original mode", "Historical payload incorrect");
    Reject(() => store.Apply(initial, id, payload), "Stale save should fail");
    Reject(() => new GameStore(cloud, () => true).Apply(changed, id, payload), "Game-running guard failed");
    var restored = history.Mode(first!, id);
    store.Apply(changed, id, restored);
    history.Capture(store.Read(), "Restore previous revision");
    Check(history.Revisions().Length == 3, "Restore rewound history");
    var restoredSnap = store.Read();
    var inPlace = Configuration.Clone(initial.Modes[0].Payload);
    Configuration.Set(inPlace, field, "66");
    File.WriteAllBytes(archive, Modes.Pack(Configuration.Wrapped(inPlace)));
    store.RestoreBackup(restoredSnap, backup);
    Check(Json.Canonical(store.Read().Modes[0].Payload) == Json.Canonical(initial.Modes[0].Payload), "Historical archives overwritten in-place were not recovered");
    Check(store.Read().Modes[0].ArchivePath != archive, "Restore overwrote a previously registered archive");
    history.Capture(store.Read(), "Restored backup");
    Reject(() => GameStore.Inside(cloud, "../outside"), "Traversal accepted");
}
using (var reopened = new History(Path.Combine(work, "History"))) { Check(reopened.Revisions().Length >= 3, "History did not survive reopen"); Check(reopened.Capture(store.Read(), "No-op") == null, "Reopen created empty commit"); }
string incomplete = Path.Combine(work, "incomplete");
Directory.CreateDirectory(incomplete);
File.WriteAllText(Path.Combine(incomplete, "CloudSaveManifest.json"), manifest.ToJsonString());
Reject(() => new GameStore(incomplete, () => false).Read(), "Incomplete scan silently dropped a mode");
string ownership = Path.Combine(work, "ownership");
Directory.CreateDirectory(ownership);
var ownedManifest = Configuration.Clone(manifest);
ownedManifest["Files"]!.AsArray().Insert(0, new JsonObject { ["FileId"] = "foreign-map", ["ContentType"] = "MapCreator", ["OwnerId"] = "foreign", ["FileName"] = "Imported map", ["Saves"] = new JsonArray() });
string ownedArchive = GameStore.Inside(ownership, relative);
Directory.CreateDirectory(Path.GetDirectoryName(ownedArchive)!);
File.Copy(archive, ownedArchive);
File.WriteAllText(Path.Combine(ownership, "CloudSaveManifest.json"), ownedManifest.ToJsonString());
var ownerStore = new GameStore(ownership, () => false);
var newOwned = Configuration.New("koth");
newOwned["Options"]!["CustomName"] = "Recipient mode";
ownerStore.Apply(ownerStore.Read(), null, newOwned);
Check((string?)ownerStore.Read().Manifest["Files"]!.AsArray().Last()!["OwnerId"] == "recipient", "Imported map owner incorrectly used");
foreach (string basis in new[] { "koth", "tdm", "swat" })
{
    var p = Configuration.New(basis);
    p["Options"]!["CustomName"] = "Mapping checks";
    foreach (var field in Configuration.Fields)
    {
        if ((string)field["id"]! == "weaponDrops")
            Configuration.Set(p, Configuration.Fields.Single(f => (string)f["id"]! == "infiniteReserves"), "False");
        var v = Configuration.Value(p, field);
        if (v == null)
            continue;
        string text = (string)field["type"]! == "boolean" ? ((bool)v ? "False" : "True") : (string)field["type"]! == "enum" ? (string)field["choices"]!.AsArray().Last()!["value"]! : Math.Clamp((double)field["default"]! + 1, (double)field["min"]!, (double)field["max"]!).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Configuration.Set(p, field, text);
        Check(Configuration.Value(p, field)?.ToString().Equals(text, StringComparison.OrdinalIgnoreCase) == true, $"Mapping lost: {basis}/{field["id"]}");
        if ((string)field["type"]! == "enum")
            Reject(() => Configuration.Set(p, field, "invalid-enum-value"), "Unknown enum accepted");
        else if ((string)field["type"]! != "boolean")
            Reject(() => Configuration.Set(p, field, ((double)field["max"]! + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)), "Range check failed");
    }
    string ItemId(string kind, string name) => (string)Configuration.Catalog[kind]!.AsArray().Single(n => (string)n!["name"]! == name)!["id"]!;
    string[] ids = [ItemId("weapons", "Cadence"), ItemId("weapons", "Fubar"), ItemId("equipment", "EMP")];
    Configuration.SetLoadout(p, ids);
    Check(Configuration.Loadout(p)!.SequenceEqual(ids), "Loadout patch failed");
    Check(Configuration.ReadArchive(Modes.Pack(Configuration.Wrapped(p))) is JsonObject, "Compiled payload pack failed");
}
var documented = Json.Read(Modes.Resource("settings-reference.json"))["settings"]!.AsArray();
Check(documented.Count == 115, "Documented reference changed without an audit");
Check(Configuration.Fields.Count(f => (bool?)f["ui"]?["extra"] == false) == 115, "Standard setting coverage is incomplete");
foreach (var reference in documented)
{
    var field = Configuration.Fields.Single(f => (string?)f["ui"]?["referenceId"] == (string?)reference!["id"]);
    Check((string?)field["ui"]?["category"] == (string?)reference!["category"], "Setting in wrong category");
    if ((string?)reference["type"] == "slider" && reference["allowedValues"] is JsonObject bounds && bounds["min"] != null && bounds["max"] != null)
        Check((double)field["min"]! <= (double)bounds["min"]! && (double)field["max"]! >= (double)bounds["max"]!, $"SGL excludes documented endpoint {reference["id"]}");
    foreach (string basis in new[] { "tdm", "koth", "swat" })
    {
        bool objectiveKillScore = basis == "koth" && ((string?)reference!["id"] is "mode.kill_score" or "mode.death_score" or "mode.assist_score" or "mode.suicide_score" or "mode.friendly_fire_score");
        Check((Configuration.Value(Configuration.New(basis), field) != null) != objectiveKillScore, $"Unexpected base mapping {basis}/{reference!["id"]}");
    }
}
var botsTest = Configuration.New("tdm");
foreach (var setting in new Dictionary<string, string> { ["botsBotsEnabled"] = "True", ["botsSpawnCustomNumBots"] = "True", ["botsCustomBotNum"] = "12", ["botDifficulty"] = "AI.Difficulty.Level.Hard", ["spawningRespawnTime"] = "2", ["spawningDeathRespawnTimePenalty"] = "-3", ["modeScoreLimit"] = "70", ["health"] = "50.5" })
    Configuration.Set(botsTest, Configuration.Fields.Single(f => (string)f["id"]! == setting.Key), setting.Value);
var botRoundTrip = Configuration.ReadArchive(Modes.Pack(Configuration.Wrapped(botsTest)));
Check(Json.Canonical(botsTest) == Json.Canonical(botRoundTrip), "Bot/spawn/numeric settings changed during export");
var enumField = Configuration.Fields.Single(f => (string)f["id"]! == "botDifficulty");
var unknownEnumOriginal = Configuration.Clone(botsTest);
foreach (var context in Configuration.Contexts(unknownEnumOriginal))
    if (context[(string)enumField["option"]!] is JsonObject bot)
        bot["Difficulty"] = "AI.Difficulty.FutureValue";
var enumEdit = Configuration.Clone(unknownEnumOriginal);
Configuration.Set(enumEdit, enumField, "AI.Difficulty.Level.Easy");
Configuration.Reset(enumEdit, unknownEnumOriginal, enumField);
Check(Json.Canonical(enumEdit) == Json.Canonical(unknownEnumOriginal), "Reset lost an unrecognized saved enum value");
var legacy = new Dictionary<string, object> { ["format"] = "spingate-mode", ["version"] = 1, ["name"] = "Legacy recipe", ["base"] = "tdm-v1", ["weapon1"] = Configuration.Catalog["weapons"]![0]!["id"]!.GetValue<string>(), ["weapon2"] = Configuration.Catalog["weapons"]![1]!["id"]!.GetValue<string>(), ["equipment"] = Configuration.Catalog["equipment"]![0]!["id"]!.GetValue<string>(), ["rules"] = new Dictionary<string, object>() };
Modes.Validate(legacy);
Check(Configuration.ReadArchive(Modes.Pack(legacy)) != null, "Legacy recipe with enum defaults no longer compiles");
Check(Modes.Summary(legacy).Contains("Legacy recipe"), "Legacy recipe summary failed with enum defaults");
foreach (string name in new[] { "Sticky-Fists-TDM", "Sticky-Fists-KOTH", "Sticky-Fists-Domination", "Ranked-Loadout-1v1", "Tempo-1v1", "Blink-1v1" })
{
    var p = (JsonObject)Json.Read(Modes.Resource(name + ".json"));
    var decoded = Configuration.ReadArchive(Modes.Pack(Configuration.Wrapped(p)));
    Check(Json.Canonical(p) == Json.Canonical(decoded), "Community payload altered");
}
using (var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256))
{
    string pub = signer.ExportSubjectPublicKeyInfoPem();
    byte[] package = [77, 90, 1, 2, 3];
    string staged = Path.Combine(work, "update.exe");
    File.WriteAllBytes(staged, package);
    byte[] content = Encoding.UTF8.GetBytes(new JsonObject { ["version"] = "0.2.0", ["url"] = "https://github.com/SGLifers/SGL/releases/download/v0.2.0/SGL.exe", ["sha256"] = Json.Hash(package), ["size"] = package.Length }.ToJsonString());
    string envelope = new JsonObject { ["payload"] = Convert.ToBase64String(content), ["signature"] = Convert.ToBase64String(signer.SignData(content, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) }.ToJsonString();
    var release = Updates.Verify(envelope, pub);
    Check(Updates.CheckFile(staged, release), "Verified package rejected");
    File.AppendAllText(staged, "tamper");
    Check(!Updates.CheckFile(staged, release), "Corrupt package accepted");
    Reject(() => Updates.EnsureWindowsExecutable(staged), "Malformed executable was accepted");
    var download = await Updates.Download(new Version(0, 1, 0), Path.Combine(work, "download"), pub, handler: new FixtureHttp(envelope, package));
    Check(download != null && File.ReadAllBytes(download.Value.Package).SequenceEqual(package), "Verified update download failed");
    Check(await Updates.Download(new Version(0, 2, 0), Path.Combine(work, "no-update"), pub, handler: new FixtureHttp(envelope, package)) == null, "Current version downloaded an update");
    bool rejectedDownload = false;
    try
    {
        await Updates.Download(new Version(0, 1, 0), Path.Combine(work, "corrupt-download"), pub, handler: new FixtureHttp(envelope, [1, 2, 3, 4, 5]));
    }
    catch { rejectedDownload = true; }
    Check(rejectedDownload, "Corrupt HTTP package was accepted");
    var modified = Json.Read(envelope);
    modified["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"));
    Reject(() => Updates.Verify(modified.ToJsonString(), pub), "Unsigned manifest accepted");
    content = Encoding.UTF8.GetBytes(new JsonObject { ["version"] = "0.2.0", ["url"] = "https://example.com/evil.exe", ["sha256"] = Json.Hash(package), ["size"] = package.Length }.ToJsonString());
    var bad = new JsonObject { ["payload"] = Convert.ToBase64String(content), ["signature"] = Convert.ToBase64String(signer.SignData(content, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) };
    Reject(() => Updates.Verify(bad.ToJsonString(), pub), "Unofficial download URL accepted");
}
byte[] MapFixture(string world = "World.cf1047")
{
    using var output = new MemoryStream();
    using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
    {
        using (var writer = new StreamWriter(zip.CreateEntry(world).Open()))
            writer.Write("test map data");
        using (var writer = new StreamWriter(zip.CreateEntry("info.json").Open(), Encoding.UTF8))
            writer.Write("{\"name\":\"Fixture arena\",\"author\":\"Test architect\"}");
    }
    return output.ToArray();
}
byte[] mapBytes = MapFixture();
Check(Maps.Inspect(mapBytes).Name == "Fixture arena", "Map metadata not read");
Reject(() => Maps.Inspect(Modes.Pack(Configuration.Wrapped(Configuration.New("tdm")))), "Mode archive accepted as map");
Reject(() => Maps.Inspect(MapFixture("Prefab.cf1047_prefab")), "Prefab accepted as map");
Reject(() => Maps.Inspect(MapFixture("../World.cf1047")), "Unsafe map entry accepted");
foreach (string badId in new[] { "../map", "C:/map", "a/b", "a\\b", "CON", "LPT1", "map." })
    Check(!Maps.ValidId(badId), "Unsafe map identity accepted: " + badId);
string mapCatalogJson = "[{\"id\":\"test_arena\",\"name\":\"Fixture arena\",\"author\":\"Architect\",\"category\":\"Playable\",\"download\":\"https://cdn.jsdelivr.net/gh/Splitgate-Architects/Splitgate-CommunityMaps@master/Maps/Playable/test_arena.bin\",\"image\":null}]";
var mapCatalog = Maps.ParseCatalog(mapCatalogJson);
Check(mapCatalog.Single().Download.StartsWith("https://raw.githubusercontent.com/"), "CDN URL not normalized to repository");
Reject(() => Maps.ParseCatalog(mapCatalogJson.Replace("test_arena.bin", "different.bin")), "Mismatched map identity accepted");
Reject(() => Maps.OfficialAsset("https://example.com/arena.bin", ".bin"), "Foreign map download allowed");
Reject(() => Maps.OfficialAsset("https://raw.githubusercontent.com/Splitgate-Architects/Splitgate-CommunityMaps/master/Maps/%2e%2e/arena.bin", ".bin"), "Encoded traversal accepted");
string mapCache = Path.Combine(work, "Cache", "maps.json");
var fetchedMaps = await Maps.Catalog(mapCache, handler: new MapHttp(mapCatalogJson, mapBytes));
Check(fetchedMaps.Length == 1, "Map catalog download failed");
Check((await Maps.Catalog(mapCache, handler: new MapHttp("broken", []))).Length == 1, "Offline cached catalog unavailable");
Check((await Maps.Download(fetchedMaps[0], handler: new MapHttp(mapCatalogJson, mapBytes))).SequenceEqual(mapBytes), "Map download bytes changed");
bool invalidMapRejected = false;
try { await Maps.Download(fetchedMaps[0], handler: new MapHttp(mapCatalogJson, [1, 2, 3])); } catch { invalidMapRejected = true; }
Check(invalidMapRejected, "Invalid map HTTP response installed");
var beforeMap = store.Read();
byte[] beforeMapIndex = File.ReadAllBytes(index);
Reject(() => new GameStore(cloud, () => true).InstallMap(beforeMap, "blocked-map", mapBytes), "Map game-running guard failed");
int guardCalls = 0;
Reject(() => new GameStore(cloud, () => ++guardCalls > 1).InstallMap(beforeMap, "rolled-back-map", mapBytes), "Map commit game guard failed");
Check(!File.Exists(Path.Combine(cloud, "MapCreator", "rolled-back-map.bin")) && File.ReadAllBytes(index).SequenceEqual(beforeMapIndex), "Failed map transaction left a registration or file");
string mapBackup = store.InstallMap(beforeMap, "test_arena", mapBytes);
var afterMap = store.Read();
Check(afterMap.Maps.Single().Id == "test_arena", "Installed map not scanned");
Check(File.ReadAllBytes(afterMap.Maps[0].ArchivePath).SequenceEqual(mapBytes), "Installed map archive altered");
Check(File.ReadAllBytes(Path.Combine(mapBackup, "CloudSaveManifest.json")).SequenceEqual(beforeMapIndex), "Map install backup changed encoding or bytes");
Check(afterMap.Modes.Single().ArchiveHash == beforeMap.Modes.Single().ArchiveHash, "Map installation changed a mode");
Check((string?)afterMap.Manifest["Extra"] == "preserve", "Map installation lost manifest metadata");
var mapEntry = afterMap.Manifest["Files"]!.AsArray().OfType<JsonObject>().Single(f => (string?)f["ContentType"] == "MapCreator");
Check((string?)mapEntry["OwnerId"] == "recipient" && (string?)mapEntry["Saves"]![0]!["SaveId"] == "custom-map", "MapLoader registration or recipient owner incorrect");
Reject(() => store.InstallMap(afterMap, "test_arena", mapBytes), "Existing map was overwritten");
Reject(() => store.InstallMap(beforeMap, "stale-map", mapBytes), "Stale map transaction accepted");
var modeWithMap = Configuration.Clone(afterMap.Modes.Single().Payload);
modeWithMap["Options"]!["CustomName"] = "Map companion mode";
string withMapBackup = store.Apply(afterMap, afterMap.Modes.Single().Id, modeWithMap);
Check(File.Exists(Path.Combine(withMapBackup, "PreviousFiles", "MapCreator", "test_arena.bin")), "Mode backup omitted map archives");
store.RestoreBackup(store.Read(), withMapBackup);
Check(store.Read().Maps.Count == 1 && File.ReadAllBytes(store.Read().Maps.Single().ArchivePath).SequenceEqual(mapBytes), "Complete restore lost map registration or bytes");
var mapSnapshot = store.Read();
File.AppendAllText(mapSnapshot.Maps.Single().ArchivePath, "external change");
Reject(() => store.Apply(mapSnapshot, mapSnapshot.Modes.Single().Id, modeWithMap), "In-place map modification was not detected");
File.WriteAllBytes(mapSnapshot.Maps.Single().ArchivePath, mapBytes);
var links = new MapLinks(Path.Combine(work, "profile", "map-mode-links.json"));
links.Set("test_arena", id, true);
links.Set("test_arena", id, true);
links.Set("test_arena", "another-mode", true);
Check(new MapLinks(Path.Combine(work, "profile", "map-mode-links.json")).ModesFor("test_arena").Length == 2, "Map associations failed persistence or deduplication");
links.Set("test_arena", id, false);
Check(links.ModesFor("test_arena").Single() == "another-mode", "Removing map link affected another association");
string liveCatalogFixture = Path.Combine("artifacts", "references", "maps.json");
GameStore DeletionFixture(string name)
{
    string root = Path.Combine(work, name);
    Directory.CreateDirectory(root);
    var original = store.Read();
    File.Copy(index, Path.Combine(root, "CloudSaveManifest.json"));
    foreach (var file in original.Manifest["Files"]!.AsArray().OfType<JsonObject>())
        foreach (var save in file["Saves"]!.AsArray().OfType<JsonObject>())
        {
            string rel = (string)save["RelativePath"]!, target = GameStore.Inside(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(GameStore.Inside(cloud, rel), target);
        }
    return new GameStore(root, () => false);
}
var deleteModes = DeletionFixture("delete-modes");
var beforeModeDelete = deleteModes.Read();
string deletedModeId = beforeModeDelete.Modes.Single().Id;
string oldModeArchive = Path.Combine(deleteModes.Root, "CustomModes", deletedModeId, "older-unregistered.bin");
File.Copy(beforeModeDelete.Modes.Single().ArchivePath, oldModeArchive);
string flatModeArchive = Path.Combine(deleteModes.Root, "CustomModes", deletedModeId + ".bin");
File.Copy(oldModeArchive, flatModeArchive);
string unrelatedNote = Path.Combine(Path.GetDirectoryName(oldModeArchive)!, "keep.txt");
File.WriteAllText(unrelatedNote, "Unrelated note");
byte[] originalModeIndex = File.ReadAllBytes(deleteModes.ManifestPath);
Reject(() => new GameStore(deleteModes.Root, () => true).Delete(beforeModeDelete, "CustomModes", deletedModeId), "Running game allowed mode deletion");
Reject(() => deleteModes.Delete(beforeModeDelete, "MapCreatorPrefab", deletedModeId), "Unexpected content deletion allowed");
Reject(() => deleteModes.Delete(beforeModeDelete, "CustomModes", "../outside"), "Deletion allowed identity traversal");
Reject(() => deleteModes.Delete(beforeModeDelete, "CustomModes", "missing-item"), "Unknown deletion identity accepted");
int deleteGuardCalls = 0;
Reject(() => new GameStore(deleteModes.Root, () => ++deleteGuardCalls > 1).Delete(beforeModeDelete, "CustomModes", deletedModeId), "Game opening during deletion did not abort");
Check(File.ReadAllBytes(deleteModes.ManifestPath).SequenceEqual(originalModeIndex) && File.Exists(oldModeArchive), "Rejected deletion changed files or index");
using (var deletionHistory = new History(Path.Combine(work, "DeletionHistory")))
{
    string start = deletionHistory.Capture(beforeModeDelete, "Before delete")!;
    var deleted = deleteModes.Delete(beforeModeDelete, "CustomModes", deletedModeId);
    var after = deleteModes.Read();
    Check(deleted.CleanupIssues.Count == 0 && after.Modes.Count == 0, "Mode deletion failed");
    Check(after.Maps.Single().ArchiveHash == beforeModeDelete.Maps.Single().ArchiveHash, "Deleting a mode changed its map");
    Check(!File.Exists(oldModeArchive) && !File.Exists(flatModeArchive) && !File.Exists(beforeModeDelete.Modes.Single().ArchivePath), "Old mode saves could resurrect deleted item");
    Check(File.Exists(unrelatedNote), "Deletion removed an unrelated file");
    Check(File.ReadAllBytes(Path.Combine(deleted.BackupDirectory, "CloudSaveManifest.json")).SequenceEqual(originalModeIndex), "Deletion backup did not preserve index bytes");
    Check(File.Exists(Path.Combine(deleted.BackupDirectory, "RemovedFiles", "CustomModes", deletedModeId, "older-unregistered.bin")), "Older save missing from deletion backup");
    Check(File.ReadAllBytes(deleteModes.ManifestPath)[0] == 255 && (string?)after.Manifest["Extra"] == "preserve", "Deletion lost encoding or metadata");
    string finish = deletionHistory.Capture(after, "Deleted mode")!;
    Check(deletionHistory.Compare(start, finish).Contains("Removed configuration."), "Deletion missing from history");
    Check(Json.Canonical(deletionHistory.Mode(start, deletedModeId)) == Json.Canonical(beforeModeDelete.Modes.Single().Payload), "Deleted mode lost historical recovery");
    deleteModes.RestoreBackup(after, deleted.BackupDirectory);
    Check(Json.Canonical(deleteModes.Read().Modes.Single().Payload) == Json.Canonical(beforeModeDelete.Modes.Single().Payload), "Deleted mode could not be restored");
}
var deleteMaps = DeletionFixture("delete-maps");
var beforeMapDelete = deleteMaps.Read();
string deletedMapId = beforeMapDelete.Maps.Single().Id;
string flatMapArchive = Path.Combine(deleteMaps.Root, "MapCreator", deletedMapId + ".bin");
File.WriteAllBytes(flatMapArchive, mapBytes);
string nativeEdits = Path.Combine(deleteMaps.Root, "MapCreator", deletedMapId);
Directory.CreateDirectory(nativeEdits);
File.WriteAllBytes(Path.Combine(nativeEdits, "map-lab-edit.bin"), mapBytes);
var mapDeleted = deleteMaps.Delete(beforeMapDelete, "MapCreator", deletedMapId);
Check(mapDeleted.CleanupIssues.Count == 0 && deleteMaps.Read().Maps.Count == 0, "Map deletion failed");
Check(deleteMaps.Read().Modes.Single().ArchiveHash == beforeMapDelete.Modes.Single().ArchiveHash, "Map deletion changed an associated mode");
Check(!File.Exists(flatMapArchive) && Directory.GetFiles(nativeEdits, "*.bin").Length == 0, "MapLoader could resurrect deleted map or edits");
Check(File.Exists(Path.Combine(mapDeleted.BackupDirectory, "RemovedFiles", "MapCreator", deletedMapId, "map-lab-edit.bin")), "Map Lab edit not retained in deletion backup");
Check(Json.Read(File.ReadAllText(Path.Combine(mapDeleted.BackupDirectory, "operation.json")))["state"]!.GetValue<string>() == "complete", "Deletion receipt was not completed");
deleteMaps.RestoreBackup(deleteMaps.Read(), mapDeleted.BackupDirectory);
Check(deleteMaps.Read().Maps.Single().ArchiveHash == beforeMapDelete.Maps.Single().ArchiveHash, "Deleted map could not be restored");
var lockedDelete = DeletionFixture("delete-locked");
var lockedSnapshot = lockedDelete.Read();
using (var held = new FileStream(lockedSnapshot.Maps.Single().ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
{
    var result = lockedDelete.Delete(lockedSnapshot, "MapCreator", lockedSnapshot.Maps.Single().Id);
    Check(result.CleanupIssues.Count == 1 && lockedDelete.Read().Maps.Count == 0, "Locked cleanup did not report committed deletion accurately");
    Check(File.Exists(lockedSnapshot.Maps.Single().ArchivePath), "Locked file was removed unexpectedly");
    Check(Json.Read(File.ReadAllText(Path.Combine(result.BackupDirectory, "operation.json")))["state"]!.GetValue<string>() == "cleanup-pending", "Partial cleanup has no recovery receipt");
    Check(File.Exists(GameStore.Inside(Path.Combine(result.BackupDirectory, "PreviousFiles"), Path.GetRelativePath(lockedDelete.Root, lockedSnapshot.Maps.Single().ArchivePath))), "Locked deletion lost recoverable archive");
}
var sharedDelete = DeletionFixture("delete-shared");
var staleDelete = DeletionFixture("delete-stale");
var staleDeleteSnapshot = staleDelete.Read();
var staleDeleteManifest = Configuration.Clone(staleDeleteSnapshot.Manifest);
staleDeleteManifest["ChangedOutsideSGL"] = true;
File.WriteAllText(staleDelete.ManifestPath, staleDeleteManifest.ToJsonString());
Reject(() => staleDelete.Delete(staleDeleteSnapshot, "MapCreator", deletedMapId), "Deletion accepted a stale save index");
Check(staleDelete.Read().Maps.Count == 1, "Stale deletion removed a registration");
var changedArchiveDelete = DeletionFixture("delete-changed-archive");
var changedArchiveSnapshot = changedArchiveDelete.Read();
File.AppendAllText(changedArchiveSnapshot.Maps.Single().ArchivePath, "changed externally");
Reject(() => changedArchiveDelete.Delete(changedArchiveSnapshot, "MapCreator", deletedMapId), "Deletion accepted an in-place map change");
var sharedManifest = sharedDelete.Read().Manifest;
var sharedEntry = Configuration.Clone(sharedManifest["Files"]!.AsArray().OfType<JsonObject>().Single(f => (string?)f["ContentType"] == "MapCreator"));
sharedEntry["FileId"] = "another-map";
sharedManifest["Files"]!.AsArray().Add(sharedEntry);
File.WriteAllText(sharedDelete.ManifestPath, sharedManifest.ToJsonString());
var sharedBefore = sharedDelete.Read();
Reject(() => sharedDelete.Delete(sharedBefore, "MapCreator", deletedMapId), "Shared archive was deleted");
Check(sharedDelete.Read().Maps.Count == 2, "Rejected shared archive deletion changed index");
var unsafeDelete = DeletionFixture("delete-unsafe");
var unsafeManifest = unsafeDelete.Read().Manifest;
var unsafeEntry = unsafeManifest["Files"]!.AsArray().OfType<JsonObject>().Single(f => (string?)f["ContentType"] == "MapCreator");
string unrelatedRel = "OtherContent/keep.bin";
Directory.CreateDirectory(Path.Combine(unsafeDelete.Root, "OtherContent"));
File.WriteAllBytes(GameStore.Inside(unsafeDelete.Root, unrelatedRel), mapBytes);
unsafeEntry["Saves"]![0]!["RelativePath"] = unrelatedRel;
File.WriteAllText(unsafeDelete.ManifestPath, unsafeManifest.ToJsonString());
Reject(() => unsafeDelete.Delete(unsafeDelete.Read(), "MapCreator", deletedMapId), "Deletion crossed content directory boundary");
Check(File.Exists(GameStore.Inside(unsafeDelete.Root, unrelatedRel)), "Unrelated content was moved");
if (File.Exists(liveCatalogFixture)) Check(Maps.ParseCatalog(File.ReadAllText(liveCatalogFixture)).Length > 5000, "Actual community catalog rejected");
string liveMapFixture = Path.Combine("artifacts", "references", "sample-map.bin");
if (File.Exists(liveMapFixture)) Check(Maps.Inspect(File.ReadAllBytes(liveMapFixture)).Name == "Simulation Omega", "Actual map archive rejected");
Console.WriteLine($"Passed {assertions} assertions: complete settings audit, presets, Git history, backups/restoration, maps, deletion/recovery, stale-write/game guards and signed updates.");
Console.WriteLine("Fixtures: " + work);
sealed class FixtureHttp(string envelope, byte[] bytes) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = request.RequestUri!.AbsoluteUri == Updates.Feed ? new StringContent(envelope) : new ByteArrayContent(bytes) });
}
sealed class MapHttp(string catalog, byte[] bytes) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = request.RequestUri!.AbsoluteUri == Maps.CatalogUrl ? new StringContent(catalog) : new ByteArrayContent(bytes) });
}
