using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace SGL.Core;

public sealed record InstalledMod(string Id, string Version, bool Enabled);
public sealed record ModConfiguration(string Path, string Text, string Hash);

public sealed class ModStore
{
    const string ListPath = "ue4ss/Mods/mods.txt";
    readonly Func<bool> running;
    readonly Action<int>? afterWrite;
    public string Root { get; }
    public string Data { get; }
    public string BackupDirectory => Path.Combine(Data, "Backups");
    string StatePath => Path.Combine(Data, "installed.json");
    public ModStore(string folder, string dataRoot, Func<bool>? running = null, Action<int>? afterWrite = null)
    {
        Root = GameInstallation.Resolve(folder);
        if (string.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), Root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Move SGL into its own folder before managing mods. Do not run SGL from the game's DLL directory.");
        Data = Path.Combine(Path.GetFullPath(dataRoot), "Mods", Json.Hash(Encoding.UTF8.GetBytes(Root.ToUpperInvariant()))[..24]);
        this.running = running ?? Modes.GameRunning;
        this.afterWrite = afterWrite;
        SafeAbsolute(Root); SafeAbsolute(Data);
    }
    static void SafeAbsolute(string path)
    {
        for (string? part = Path.GetFullPath(path); part != null; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("Mods cannot be installed through a symbolic link or junction: " + part);
    }
    string Target(string relative)
    {
        if (relative != "dwmapi.dll" && !relative.StartsWith("ue4ss/", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path is outside the managed mod installation.");
        if (relative.Split('/').Any(p => !Maps.ValidId(p))) throw new InvalidDataException("Invalid mod file path.");
        string path = GameStore.Inside(Root, relative);
        SafeAbsolute(path);
        return path;
    }
    JsonObject State()
    {
        SafeAbsolute(StatePath);
        if (!File.Exists(StatePath)) return new() { ["root"] = Root, ["mods"] = new JsonObject(), ["runtime"] = new JsonObject() };
        if (new FileInfo(StatePath).Length > 4_000_000) throw new InvalidDataException("Mod installation receipt is too large.");
        var state = Json.Read(File.ReadAllText(StatePath)).AsObject();
        if (!string.Equals((string?)state["root"], Root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Mod receipt belongs to another installation.");
        return state;
    }
    public bool RuntimeInstalled => State()["runtime"]!.AsObject().Count > 0;
    public InstalledMod[] Installed()
    {
        return State()["mods"]!.AsObject().Select(p => new InstalledMod(p.Key, (string)p.Value!["version"]!, Enabled(p.Key))).OrderBy(p => p.Id).ToArray();
    }
    public bool Enabled(string id)
    {
        if (!Mods.ValidId(id)) throw new InvalidDataException("Invalid mod identity.");
        return File.Exists(Target("ue4ss/Mods/" + id + "/enabled.txt")) || (File.Exists(Target(ListPath)) && File.ReadAllLines(Target(ListPath)).Any(l => Regex.IsMatch(l, @"^\s*" + Regex.Escape(id) + @"\s*:\s*1\s*(?:;.*)?$", RegexOptions.IgnoreCase)));
    }
    static string SetFlag(string text, string id, bool enabled)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Where(l => !Regex.IsMatch(l, @"^\s*" + Regex.Escape(id) + @"\s*:", RegexOptions.IgnoreCase)).ToList();
        // Keep the built-in Keybinds module last, as required by UE4SS.
        int keybinds = lines.FindIndex(l => Regex.IsMatch(l, @"^\s*Keybinds\s*:", RegexOptions.IgnoreCase));
        lines.Insert(keybinds < 0 ? lines.Count : keybinds, id + " : " + (enabled ? "1" : "0"));
        return string.Join('\n', lines).TrimEnd() + "\n";
    }
    void Closed()
    {
        if (running()) throw new InvalidOperationException("Close Splitgate before installing, updating, configuring, toggling or restoring mods.");
        SafeAbsolute(Root); SafeAbsolute(Data);
    }
    void CheckRuntime(JsonObject state)
    {
        if ((string?)state["gameHash"] != Hash(Path.Combine(Root, GameInstallation.Executable))) throw new IOException("The game executable changed since UE4SS was installed. Restore the initial mod backup and verify compatibility before reinstalling.");
        foreach (var file in state["runtime"]!.AsObject())
            if (Hash(Target(file.Key)) != (string?)file.Value) throw new IOException("Managed UE4SS file changed or is missing: " + file.Key + ". Restore a mod backup before reinstalling.");
    }
    static string? Hash(string path)
    {
        if (!File.Exists(path)) return null;
        using var file = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file));
    }
    public string Install(ModBundle bundle, string id, Dictionary<string, byte[]>? runtime = null)
    {
        Closed();
        var incoming = Mods.ModFiles(bundle, id);
        var state = State();
        string expectedState = Hash(StatePath) ?? "";
        var changes = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        var observed = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        bool first = state["runtime"]!.AsObject().Count == 0;
        if (first)
        {
            if (runtime == null || !runtime.ContainsKey("dwmapi.dll") || !runtime.ContainsKey("ue4ss/UE4SS.dll")) throw new InvalidOperationException("Prepare the compatible UE4SS runtime first.");
            if (Directory.Exists(Path.Combine(Root, "ue4ss")) || File.Exists(Path.Combine(Root, "dwmapi.dll"))) throw new IOException("An existing UE4SS installation was found. SGL will not overwrite it. Back it up and move it out of the game folder before using SGL's managed installation.");
            foreach (var file in runtime) { Target(file.Key); changes.Add(file.Key, file.Value); observed[file.Key] = null; }
            state["runtime"] = new JsonObject(runtime.Where(p => p.Key != ListPath && p.Key != "ue4ss/UE4SS-settings.ini").Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key, JsonValue.Create(Json.Hash(p.Value)))));
            state["gameHash"] = Hash(Path.Combine(Root, GameInstallation.Executable));
            state["runtimeVersion"] = "UE4SS 4.0.0 + Splitgate-Mods 1.1.0 patches";
        }
        else CheckRuntime(state);
        var previous = state["mods"]![id];
        if (previous != null && Mods.Newer((string)previous["version"]!, bundle.Release.Version)) throw new InvalidOperationException("An older catalog cannot downgrade this mod. Refresh the catalog first.");
        var oldFiles = previous?["files"]?.AsObject();
        bool enabled = previous == null || Enabled(id);
        var receipts = new JsonObject();
        foreach (var file in incoming.Where(p => !p.Key.EndsWith("/enabled.txt", StringComparison.OrdinalIgnoreCase)))
        {
            string path = Target(file.Key);
            string? current = Hash(path), old = (string?)oldFiles?[file.Key];
            observed[file.Key] = current;
            if (current != null && old == null) throw new IOException("An unmanaged file already exists: " + file.Key);
            if (current != null && Mods.ConfigurationFile(file.Key)) { receipts[file.Key] = current; continue; }
            if (current != null && current != old) throw new IOException("Locally edited mod code would be overwritten: " + file.Key + ". Restore or back up that edit first.");
            changes[file.Key] = file.Value;
            receipts[file.Key] = Json.Hash(file.Value);
        }
        if (oldFiles != null)
            foreach (var file in oldFiles.Where(p => !receipts.ContainsKey(p.Key)))
            {
                if (Mods.ConfigurationFile(file.Key)) { receipts[file.Key] = file.Value?.DeepClone(); continue; }
                string? current = Hash(Target(file.Key));
                if (current != null && current != (string?)file.Value) throw new IOException("A removed mod file has local edits: " + file.Key);
                observed[file.Key] = current;
                changes[file.Key] = null;
            }
        string marker = "ue4ss/Mods/" + id + "/enabled.txt";
        observed[marker] = Hash(Target(marker)); changes[marker] = null;
        byte[]? listBytes = changes.TryGetValue(ListPath, out var initial) ? initial : File.Exists(Target(ListPath)) ? File.ReadAllBytes(Target(ListPath)) : null;
        string list = listBytes == null ? "" : Encoding.UTF8.GetString(listBytes);
        if (!first) observed[ListPath] = listBytes == null ? null : Json.Hash(listBytes);
        changes[ListPath] = Encoding.UTF8.GetBytes(SetFlag(list, id, enabled));
        state["mods"]![id] = new JsonObject { ["version"] = bundle.Release.Version, ["source"] = bundle.Release.Download, ["releaseHash"] = bundle.Release.Sha256, ["files"] = receipts };
        return Commit(changes, state, expectedState, (previous == null ? "Install " : "Update ") + id, observed);
    }
    public string Toggle(string id, bool enabled)
    {
        Closed();
        var state = State();
        string expected = Hash(StatePath) ?? "";
        if (!Mods.ValidId(id) || state["mods"]![id] == null) throw new InvalidOperationException("Install this mod with SGL first.");
        if (enabled) CheckRuntime(state);
        if (enabled && !File.Exists(Target("ue4ss/Mods/" + id + "/Scripts/main.lua"))) throw new IOException("The mod entry point is missing. Reinstall it first.");
        byte[]? listBytes = File.Exists(Target(ListPath)) ? File.ReadAllBytes(Target(ListPath)) : null;
        string text = listBytes == null ? "" : Encoding.UTF8.GetString(listBytes);
        string marker = "ue4ss/Mods/" + id + "/enabled.txt";
        return Commit(new(StringComparer.OrdinalIgnoreCase) { [ListPath] = Encoding.UTF8.GetBytes(SetFlag(text, id, enabled)), [marker] = null }, state, expected, (enabled ? "Enable " : "Disable ") + id,
            new(StringComparer.OrdinalIgnoreCase) { [ListPath] = listBytes == null ? null : Json.Hash(listBytes), [marker] = Hash(Target(marker)) });
    }
    public ModConfiguration[] Configurations(string id)
    {
        if (!Mods.ValidId(id)) throw new InvalidDataException("Invalid mod identity.");
        var files = State()["mods"]?[id]?["files"]?.AsObject();
        return files?.Where(p => Mods.ConfigurationFile(p.Key) && (p.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || id == "MapEditorBudgetMod") && File.Exists(Target(p.Key))).Select(p =>
        {
            byte[] bytes = File.ReadAllBytes(Target(p.Key));
            if (bytes.Length > 1_000_000) throw new InvalidDataException("Mod configuration exceeds 1 MB.");
            return new ModConfiguration(p.Key, Encoding.UTF8.GetString(bytes), Json.Hash(bytes));
        }).OrderBy(p => p.Path).ToArray() ?? [];
    }
    public string Configure(string id, ModConfiguration expected, string text)
    {
        Closed();
        var state = State();
        string receiptHash = Hash(StatePath) ?? "";
        if (state["mods"]?[id]?["files"]?[expected.Path] == null || !Mods.ConfigurationFile(expected.Path)) throw new InvalidDataException("Configuration is not managed by this mod.");
        if (Hash(Target(expected.Path)) != expected.Hash) throw new IOException("This configuration changed outside SGL. Reload it before saving.");
        if (Encoding.UTF8.GetByteCount(text) > 1_000_000) throw new InvalidDataException("Mod configuration exceeds 1 MB.");
        if (expected.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = Json.Read(text);
            JsonNode? original = null;
            try { original = Json.Read(expected.Text); } catch (System.Text.Json.JsonException) { }
            if (original != null && ((parsed is JsonObject) != (original is JsonObject) || (parsed is JsonArray) != (original is JsonArray))) throw new InvalidDataException("Keep the original JSON object or array structure.");
        }
        else
        {
            if (id != "MapEditorBudgetMod" || !Regex.IsMatch(text, @"\A\s*(?:--[^\r\n]*(?:\r?\n|\z)\s*)*return\s*\{\s*(?:--[^\r\n]*\r?\n\s*)*BudgetLimit\s*=\s*[1-9][0-9]{0,6}\s*,?\s*\}\s*\z")) throw new InvalidDataException("Budget configuration must return a table with BudgetLimit between 1 and 9999999.");
        }
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        state["mods"]![id]!["files"]![expected.Path] = Json.Hash(bytes);
        return Commit(new(StringComparer.OrdinalIgnoreCase) { [expected.Path] = bytes }, state, receiptHash, "Configure " + id, new(StringComparer.OrdinalIgnoreCase) { [expected.Path] = expected.Hash });
    }
    static void Write(string path, byte[]? bytes)
    {
        SafeAbsolute(path);
        if (bytes == null) { if (File.Exists(path)) File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".sgl-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    void PruneEmptyParents(IEnumerable<string> files)
    {
        foreach (string file in files)
            for (string? parent = Path.GetDirectoryName(file); parent != null && parent != Root && parent.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); parent = Path.GetDirectoryName(parent))
            {
                SafeAbsolute(parent);
                if (!Directory.Exists(parent)) continue;
                if (Directory.EnumerateFileSystemEntries(parent).Any()) break;
                Directory.Delete(parent);
            }
    }
    string Commit(Dictionary<string, byte[]?> changes, JsonObject state, string expectedState, string action, Dictionary<string, string?>? observed = null)
    {
        Closed();
        SafeAbsolute(BackupDirectory);
        Directory.CreateDirectory(Data);
        using var operation = new FileStream(Path.Combine(Data, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if ((Hash(StatePath) ?? "") != expectedState) throw new IOException("Mod installation changed during preparation. Refresh and retry.");
        var targets = changes.ToDictionary(p => p.Key, p => Target(p.Key), StringComparer.OrdinalIgnoreCase);
        changes["@state"] = Encoding.UTF8.GetBytes(state.ToJsonString()); targets["@state"] = StatePath;
        var before = targets.ToDictionary(p => p.Key, p => File.Exists(p.Value) ? File.ReadAllBytes(p.Value) : null, StringComparer.OrdinalIgnoreCase);
        if (observed != null)
            foreach (var file in observed)
                if (Hash(Target(file.Key)) != file.Value) throw new IOException("Mod file changed while preparing: " + file.Key);
        string backup = Path.Combine(BackupDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(backup);
        var entries = new JsonArray();
        int index = 0;
        foreach (var file in changes)
        {
            string name = (index++).ToString("D5") + ".bin";
            if (before[file.Key] is byte[] bytes) File.WriteAllBytes(Path.Combine(backup, name), bytes);
            entries.Add(new JsonObject { ["path"] = file.Key, ["backup"] = name, ["before"] = before[file.Key] == null ? null : Json.Hash(before[file.Key]!), ["after"] = file.Value == null ? null : Json.Hash(file.Value) });
        }
        var journal = new JsonObject { ["root"] = Root, ["action"] = action, ["status"] = "prepared", ["files"] = entries };
        string journalPath = Path.Combine(backup, "backup.json");
        Json.Atomic(journalPath, journal.ToJsonString());
        var written = new List<string>();
        try
        {
            Closed();
            foreach (var file in changes)
            {
                Closed();
                if (Hash(targets[file.Key]) != (before[file.Key] == null ? null : Json.Hash(before[file.Key]!))) throw new IOException("Mod file changed during installation: " + file.Key);
                Write(targets[file.Key], file.Value); written.Add(file.Key); afterWrite?.Invoke(written.Count);
            }
            journal["status"] = "complete"; Json.Atomic(journalPath, journal.ToJsonString());
            PruneEmptyParents(changes.Where(p => p.Key != "@state" && p.Value == null).Select(p => targets[p.Key]));
            return backup;
        }
        catch (Exception error)
        {
            var failures = new List<string>();
            foreach (string key in written.AsEnumerable().Reverse())
            {
                try
                {
                    string? actual = Hash(targets[key]);
                    if (actual == (before[key] == null ? null : Json.Hash(before[key]!))) continue;
                    if (actual != (changes[key] == null ? null : Json.Hash(changes[key]!))) throw new IOException("Changed externally");
                    Write(targets[key], before[key]);
                }
                catch { failures.Add(key); }
            }
            journal["status"] = failures.Count == 0 ? "rolled-back" : "recovery-needed";
            if (failures.Count == 0) PruneEmptyParents(written.Where(k => k != "@state" && before[k] == null).Select(k => targets[k]));
            journal["recoveryFiles"] = new JsonArray(failures.Select(p => JsonValue.Create(p)).ToArray());
            Json.Atomic(journalPath, journal.ToJsonString());
            throw new IOException("Mod operation failed. " + (failures.Count == 0 ? "Changes were rolled back." : "Some files need recovery from the backup.") + " Backup: " + backup, error);
        }
    }
    public string[] Backups() => Directory.Exists(BackupDirectory) ? Directory.GetDirectories(BackupDirectory).Where(p => File.Exists(Path.Combine(p, "backup.json"))).OrderDescending().ToArray() : [];
    public string Restore(string backup)
    {
        Closed();
        backup = Path.GetFullPath(backup);
        if (!Backups().Contains(backup, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Choose a backup for this game installation.");
        SafeAbsolute(backup);
        var journal = Json.Read(File.ReadAllText(Path.Combine(backup, "backup.json")));
        if ((string?)journal["root"] != Root || (string?)journal["status"] != "complete") throw new InvalidDataException("Backup is not a completed operation for this installation.");
        var changes = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        var observed = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        JsonObject? restoredState = null;
        string currentState = Hash(StatePath) ?? "";
        foreach (var file in journal["files"]!.AsArray())
        {
            string key = (string)file!["path"]!, target = key == "@state" ? StatePath : Target(key);
            if (Hash(target) != (string?)file["after"]) throw new IOException("A file changed after this backup. Restore newer operations first: " + key);
            if (key != "@state") observed[key] = (string?)file["after"];
            byte[]? bytes = null;
            if (file["before"] != null)
            {
                string name = (string)file["backup"]!;
                if (!Regex.IsMatch(name, @"\A[0-9]{5}\.bin\z")) throw new InvalidDataException("Invalid backup entry.");
                string saved = GameStore.Inside(backup, name); SafeAbsolute(saved);
                bytes = File.ReadAllBytes(saved);
                if (Json.Hash(bytes) != (string?)file["before"]) throw new InvalidDataException("Mod backup checksum mismatch.");
            }
            if (key == "@state") restoredState = bytes == null ? new JsonObject { ["root"] = Root, ["mods"] = new JsonObject(), ["runtime"] = new JsonObject() } : Json.Read(Encoding.UTF8.GetString(bytes)).AsObject();
            else changes[key] = bytes;
        }
        if (restoredState == null) throw new InvalidDataException("Backup has no installation receipt.");
        return Commit(changes, restoredState, currentState, "Restore " + Path.GetFileName(backup), observed);
    }
}
