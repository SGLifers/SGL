using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SGL.Core;

static class ModChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
        void Reject(Action action, string message) { bool rejected = false; try { action(); } catch { rejected = true; } check(rejected, message); }
        async Task RejectAsync(Func<Task> action, string message) { bool rejected = false; try { await action(); } catch { rejected = true; } check(rejected, message); }
        string temp = Path.Combine(Path.GetTempPath(), "sgl-mod-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Dictionary<string, byte[]> Runtime() => new(StringComparer.OrdinalIgnoreCase) { ["dwmapi.dll"] = Bytes("proxy fixture"), ["ue4ss/UE4SS.dll"] = Bytes("patched fixture"), ["ue4ss/Mods/mods.txt"] = Bytes("; comments retained\nKeybinds : 1\n"), ["ue4ss/LICENSE"] = Bytes("license") };
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ue4ss/Mods/BotChatter/Scripts/main.lua"] = Bytes("-- bot fixture"),
            ["ue4ss/Mods/BotChatter/config.json"] = Bytes("{\"enabled\":true,\"host_only\":true,\"chance\":0.3}"),
            ["ue4ss/Mods/BotChatter/phrases.json"] = Bytes("{\"kill\":[\"hello\"]}"),
            ["ue4ss/Mods/BotChatter/enabled.txt"] = [],
            ["ue4ss/Mods/MapEditorBudgetMod/scripts/main.lua"] = Bytes("-- budget fixture"),
            ["ue4ss/Mods/MapEditorBudgetMod/scripts/config.lua"] = Bytes("-- budget\nreturn {\n -- Default value\n BudgetLimit = 10000,\n}\n"),
            ["ue4ss/Mods/FutureMod/Scripts/main.lua"] = Bytes("-- future mod"),
            ["ue4ss/Mods/FutureMod/config.json"] = Bytes("{\"value\":5}"),
            ["ue4ss/Mods/shared/data.json"] = Bytes("{}")
        };
        ModBundle Bundle(string version = "v1.1.0", Dictionary<string, byte[]>? source = null) => new(new(version, Mods.BootstrapUrl, Mods.BootstrapHash, Mods.Discover(source ?? files)), source ?? files);
        check(Mods.Discover(files).Select(m => m.Id).Order().SequenceEqual(new[] { "BotChatter", "FutureMod", "MapEditorBudgetMod" }), "New mods were not discovered or shared support files appeared as a mod");
        check(Mods.ModFiles(Bundle(), "BotChatter").Count == 4, "Mod file selection crossed package boundaries");
        var native = new Dictionary<string, byte[]>(files) { ["ue4ss/Mods/FutureMod/mod.dll"] = [1] };
        Reject(() => Mods.ModFiles(Bundle(source: native), "FutureMod"), "Unreviewed native DLL was installed through Lua discovery");
        byte[] Zip(Dictionary<string, byte[]> contents)
        {
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                foreach (var item in contents) using (var file = zip.CreateEntry(item.Key).Open()) file.Write(item.Value);
            return output.ToArray();
        }
        foreach (string path in new[] { "../outside.lua", "ue4ss/Mods/../evil.lua", "C:/evil.lua", "/absolute.lua", "ue4ss\\Mods\\evil.lua", "ue4ss/Mods/CON.lua", "ue4ss/Mods/bad./main.lua" })
            Reject(() => Mods.ReadZip(Zip(new() { [path] = [1] })), "Accepted unsafe ZIP path: " + path);
        Reject(() => Mods.ReadZip(Zip(new() { ["same.lua"] = [1], ["SAME.lua"] = [2] })), "Accepted case-insensitive duplicate archive paths");
        string Metadata(string hash, string url = Mods.BootstrapUrl) => new JsonObject { ["tag_name"] = "v1.1.0", ["draft"] = false, ["prerelease"] = false, ["assets"] = new JsonArray(new JsonObject { ["name"] = "Splitgate-Mods.zip", ["browser_download_url"] = url, ["digest"] = "sha256:" + hash }) }.ToJsonString();
        Reject(() => Mods.ReleaseMetadata(Metadata(Mods.BootstrapHash, "https://evil.example/mods.zip")), "Foreign release source accepted");
        Reject(() => Mods.ReleaseMetadata(Metadata("bad")), "Missing digest accepted");
        Reject(() => Mods.ReleaseMetadata(Metadata(Mods.BootstrapHash).Replace("v1.1.0\",\"draft", "v2.0.0\",\"draft")), "Release/asset mismatch accepted");
        byte[] package = Zip(files);
        using var handler = new Transport(Metadata(Json.Hash(package)), package);
        string cache = Path.Combine(temp, "cache");
        var downloaded = await Mods.Catalog(cache, true, handler: handler);
        check(downloaded.Release.Mods.Length == 3 && handler.Requests == 2, "Live catalog did not inspect the published ZIP");
        handler.Fail = true;
        check((await Mods.Catalog(cache, false, handler: handler)).Release.Mods.Length == 3 && handler.Requests == 2, "Offline catalog unexpectedly used the network");
        await RejectAsync(() => Mods.Catalog(cache, true, handler: handler), "Unavailable refresh was silently accepted");
        File.WriteAllBytes(Path.Combine(cache, Json.Hash(package) + ".zip"), [5]);
        await RejectAsync(() => Mods.Catalog(cache, false, handler: handler), "Corrupt cached executable mod package was accepted");

        ModStore Store(string name, Func<bool>? running = null, Action<int>? fault = null)
        {
            string root = Path.Combine(temp, name, "PortalWars2", "Binaries", "Win64");
            Directory.CreateDirectory(root); File.WriteAllBytes(Path.Combine(root, GameInstallation.Executable), Bytes("game fixture"));
            return new ModStore(root, Path.Combine(temp, "data"), running ?? (() => false), fault);
        }
        var store = Store("main");
        check(GameInstallation.Resolve(Path.GetDirectoryName(Path.GetDirectoryName(store.Root)!)!) == store.Root, "Game-root resolution failed");
        File.WriteAllBytes(Path.Combine(store.Root, "version.dll"), Bytes("unrelated wall running"));
        string backup = store.Install(Bundle(), "BotChatter", Runtime());
        check(store.RuntimeInstalled && store.Installed() is [{ Id: "BotChatter", Enabled: true }], "First enable did not install only the selected mod");
        check(!Directory.Exists(Path.Combine(store.Root, "ue4ss/Mods/MapEditorBudgetMod")), "Another mod was enabled/installed implicitly");
        check(File.ReadAllText(Path.Combine(store.Root, "version.dll")) == "unrelated wall running", "Unrelated proxy DLL was changed");
        string list = Path.Combine(store.Root, "ue4ss/Mods/mods.txt"), marker = Path.Combine(store.Root, "ue4ss/Mods/BotChatter/enabled.txt");
        File.AppendAllText(list, "Unmanaged : 1\nBotChatter : 1\n"); File.WriteAllText(marker, "manual marker");
        string disabledBackup = store.Toggle("BotChatter", false);
        check(!store.Enabled("BotChatter") && !File.Exists(marker), "Disable left enabled.txt active");
        check(File.ReadAllText(list).Contains("Unmanaged : 1") && File.ReadAllText(list).Contains("comments retained") && File.ReadAllLines(list).Count(l => l.StartsWith("BotChatter :")) == 1, "Toggle destroyed unrelated entries or retained duplicate flags");
        check(File.ReadAllText(list).IndexOf("BotChatter :", StringComparison.Ordinal) < File.ReadAllText(list).IndexOf("Keybinds :", StringComparison.Ordinal), "Keybinds load order was changed");
        store.Restore(disabledBackup);
        check(store.Enabled("BotChatter") && File.ReadAllText(marker) == "manual marker", "Restore did not retain the original marker");
        var config = store.Configurations("BotChatter").Single(c => c.Path.EndsWith("config.json"));
        string configuredBackup = store.Configure("BotChatter", config, "{\"enabled\":true,\"host_only\":true,\"chance\":0.99}");
        check(store.Configurations("BotChatter").Single(c => c.Path == config.Path).Text.Contains("0.99"), "Bot configuration did not save");
        Reject(() => store.Configure("BotChatter", config, "{}"), "Stale configuration overwrote newer data");
        var current = store.Configurations("BotChatter").Single(c => c.Path == config.Path);
        Reject(() => store.Configure("BotChatter", current, "{"), "Invalid JSON configuration accepted");
        Reject(() => store.Configure("BotChatter", current, "[]"), "Configuration object changed to array");
        var updatedFiles = new Dictionary<string, byte[]>(files) { ["ue4ss/Mods/BotChatter/Scripts/main.lua"] = Bytes("-- updated code"), [config.Path] = Bytes("{\"chance\":0.1}") };
        store.Toggle("BotChatter", false);
        store.Install(Bundle("v1.2.0", updatedFiles), "BotChatter");
        check(!store.Enabled("BotChatter") && store.Configurations("BotChatter").Single(c => c.Path == config.Path).Text.Contains("0.99"), "Update reset enable state or personal configuration");
        File.AppendAllText(Path.Combine(store.Root, "ue4ss/Mods/BotChatter/Scripts/main.lua"), "-- local edit");
        Reject(() => store.Install(Bundle("v1.3.0"), "BotChatter"), "Update overwrote locally edited code");
        store.Install(Bundle(), "MapEditorBudgetMod");
        var budget = store.Configurations("MapEditorBudgetMod").Single();
        store.Configure("MapEditorBudgetMod", budget, budget.Text.Replace("10000", "12500"));
        check(store.Configurations("MapEditorBudgetMod").Single().Text.Contains("12500"), "Map budget configuration did not save");
        Reject(() => store.Configure("MapEditorBudgetMod", store.Configurations("MapEditorBudgetMod").Single(), "os.execute('evil')"), "Lua configuration editor accepted arbitrary code");
        File.AppendAllText(Path.Combine(store.Root, GameInstallation.Executable), "new game build");
        Reject(() => store.Toggle("BotChatter", true), "Changed game build was enabled without verification");
        store.Toggle("MapEditorBudgetMod", false);
        check(!store.Enabled("MapEditorBudgetMod"), "Game-build mismatch prevented disabling a mod");

        var busy = Store("running", () => true);
        Reject(() => busy.Install(Bundle(), "BotChatter", Runtime()), "Installed while Splitgate was running");
        check(!Directory.Exists(Path.Combine(busy.Root, "ue4ss")), "Running-game rejection wrote game files");
        var conflict = Store("conflict"); File.WriteAllText(Path.Combine(conflict.Root, "dwmapi.dll"), "existing");
        Reject(() => conflict.Install(Bundle(), "BotChatter", Runtime()), "Unmanaged UE4SS proxy was overwritten");
        check(File.ReadAllText(Path.Combine(conflict.Root, "dwmapi.dll")) == "existing", "Existing proxy changed after failure");
        var failing = Store("failure", fault: count => { if (count == 2) throw new IOException("Injected disk failure"); });
        Reject(() => failing.Install(Bundle(), "BotChatter", Runtime()), "Injected installation failure was ignored");
        check(!File.Exists(Path.Combine(failing.Root, "dwmapi.dll")) && !File.Exists(Path.Combine(failing.Root, "ue4ss/UE4SS.dll")) && !failing.RuntimeInstalled, "Rollback left a partial runtime installed");
        check(failing.Backups().Length == 1, "Failed installation lost its recovery journal");
        check(!Directory.Exists(Path.Combine(failing.Root, "ue4ss")), "Failed first install left directories that would prevent retry");
        new ModStore(failing.Root, Path.Combine(temp, "data"), () => false).Install(Bundle(), "BotChatter", Runtime());
        check(failing.RuntimeInstalled, "Rolled-back installation could not be retried");
        bool started = false;
        var launchRace = Store("launch-race", () => started, count => { if (count == 1) started = true; });
        Reject(() => launchRace.Install(Bundle(), "BotChatter", Runtime()), "Game starting mid-install was ignored");
        check(!File.Exists(Path.Combine(launchRace.Root, "dwmapi.dll")), "Game-start race left the proxy partially installed");
        var changed = Store("changed-during-rollback");
        var conflictRollback = new ModStore(changed.Root, Path.Combine(temp, "data"), () => false, count =>
        {
            if (count == 1) { File.WriteAllText(Path.Combine(changed.Root, "dwmapi.dll"), "external replacement"); throw new IOException("Injected concurrent edit"); }
        });
        Reject(() => conflictRollback.Install(Bundle(), "BotChatter", Runtime()), "Concurrent replacement was ignored");
        check(File.ReadAllText(Path.Combine(changed.Root, "dwmapi.dll")) == "external replacement", "Rollback erased a concurrent external edit");
        check((string?)Json.Read(File.ReadAllText(Path.Combine(conflictRollback.Backups().Single(), "backup.json")))["status"] == "recovery-needed", "Rollback conflict lost its recovery status");
        var restore = Store("restore");
        string initial = restore.Install(Bundle(), "BotChatter", Runtime());
        string toggled = restore.Toggle("BotChatter", false);
        Reject(() => restore.Restore(initial), "Old backup overwrote a more recent operation");
        restore.Restore(toggled); restore.Restore(initial);
        check(!restore.RuntimeInstalled && !File.Exists(Path.Combine(restore.Root, "dwmapi.dll")), "Initial backup did not remove managed runtime");
        restore.Install(Bundle(), "FutureMod", Runtime());
        check(restore.Installed() is [{ Id: "FutureMod", Enabled: true }], "Discovered future mod required a hardcoded installer");
        restore.Install(Bundle("v1.2.0"), "FutureMod");
        Reject(() => restore.Install(Bundle("v1.1.0"), "FutureMod"), "Stale offline catalog downgraded a newer installed mod");
        File.WriteAllText(Path.Combine(restore.Root, "ue4ss/UE4SS.dll"), "different runtime");
        restore.Toggle("FutureMod", false);
        Reject(() => restore.Toggle("FutureMod", true), "Changed runtime was silently enabled");
        string brokenConfig = Path.Combine(restore.Root, "ue4ss/Mods/FutureMod/config.json");
        File.WriteAllText(brokenConfig, "{broken");
        restore.Configure("FutureMod", restore.Configurations("FutureMod").Single(), "{\"value\":6}");
        check(File.ReadAllText(brokenConfig).Contains("6"), "Invalid external JSON could not be repaired in the configuration editor");
        check(store.Backups().All(p => File.Exists(Path.Combine(p, "backup.json"))), "Mod operation missed its backup");
    }
    sealed class Transport(string metadata, byte[] package) : HttpMessageHandler
    {
        public int Requests; public bool Fail;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail) throw new HttpRequestException("Offline fixture");
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = request.RequestUri!.AbsoluteUri == Mods.Latest ? new StringContent(metadata) : new ByteArrayContent(package) });
        }
    }
}
