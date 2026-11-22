using System.Text;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
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
ownedManifest["Files"]!.AsArray().Insert(0, new JsonObject { ["ContentType"] = "MapCreator", ["OwnerId"] = "foreign", ["FileName"] = "Imported map", ["Saves"] = new JsonArray() });
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
        string text = (string)field["type"]! == "boolean" ? ((bool)v ? "False" : "True") : Math.Clamp((double)field["default"]! + 1, (double)field["min"]!, (double)field["max"]!).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Configuration.Set(p, field, text);
        Check(Configuration.Value(p, field) != null, "Mapping lost");
        if ((string)field["type"]! != "boolean")
            Reject(() => Configuration.Set(p, field, ((double)field["max"]! + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)), "Range check failed");
    }
    string ItemId(string kind, string name) => (string)Configuration.Catalog[kind]!.AsArray().Single(n => (string)n!["name"]! == name)!["id"]!;
    string[] ids = [ItemId("weapons", "Cadence"), ItemId("weapons", "Fubar"), ItemId("equipment", "EMP")];
    Configuration.SetLoadout(p, ids);
    Check(Configuration.Loadout(p)!.SequenceEqual(ids), "Loadout patch failed");
    Check(Configuration.ReadArchive(Modes.Pack(Configuration.Wrapped(p))) is JsonObject, "Compiled payload pack failed");
}
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
Console.WriteLine($"Passed {assertions} assertions: mapping preservation, six presets, Git history, no-op scans, rename identity, semantic diffs, forward restoration, byte-exact backups, UTF16, stale-write/game guards and signed updates.");
Console.WriteLine("Fixtures: " + work);
sealed class FixtureHttp(string envelope, byte[] bytes) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = request.RequestUri!.AbsoluteUri == Updates.Feed ? new StringContent(envelope) : new ByteArrayContent(bytes) });
}
