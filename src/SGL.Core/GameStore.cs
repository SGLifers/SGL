using System.Text;
using System.Text.Json.Nodes;
namespace SGL.Core;

public sealed record SavedMode(string Id, string Name, JsonObject Payload, string ArchivePath, string ArchiveHash)
{
    public override string ToString() => Name;
}
public sealed record GameSnapshot(string ManifestHash, JsonObject Manifest, IReadOnlyList<SavedMode> Modes)
{
    public IReadOnlyList<SavedMap> Maps { get; init; } = [];
}
public sealed partial class GameStore(string root, Func<bool>? running = null)
{
    public string Root { get; } = Path.GetFullPath(root);
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortalWars2", "Saved", "Cloud", "CloudSave");
    public string ManifestPath => Path.Combine(Root, "CloudSaveManifest.json");
    public static string Inside(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Invalid save path.");
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Save path escapes its folder.");
        return path;
    }
    public GameSnapshot Read()
    {
        byte[] original = File.ReadAllBytes(ManifestPath);
        if (original.Length > 16_000_000)
            throw new InvalidDataException("Save index exceeds 16 MB.");
        JsonObject manifest;
        using (var reader = new StreamReader(new MemoryStream(original), true))
            manifest = (JsonObject)Json.Read(reader.ReadToEnd());
        var files = manifest["Files"]?.AsArray() ?? throw new InvalidDataException("Save index has no Files array.");
        var modes = new List<SavedMode>();
        var maps = new List<SavedMap>();
        foreach (var file in files.OfType<JsonObject>().Where(f => (string?)f["ContentType"] == "CustomModes"))
        {
            string id = (string?)file["FileId"] ?? throw new InvalidDataException("Mode has no identity.");
            if (modes.Any(m => m.Id == id))
                throw new InvalidDataException("Duplicate mode identity in save index.");
            var saves = file["Saves"]?.AsArray().OfType<JsonObject>().OrderByDescending(s => (string?)s["CreatedAt"], StringComparer.Ordinal).ToArray() ?? [];
            if (saves.Length == 0)
                continue;
            string path = Inside(Root, (string?)saves[0]["RelativePath"] ?? "");
            byte[] bytes = File.ReadAllBytes(path);
            var p = Configuration.ReadArchive(bytes);
            if (Json.Hash(bytes) != Json.Hash(File.ReadAllBytes(path)))
                throw new IOException("A mode changed during scanning. Refresh to retry.");
            modes.Add(new(id, Configuration.Name(p), p, path, Json.Hash(bytes)));
        }
        foreach (var file in files.OfType<JsonObject>().Where(f => (string?)f["ContentType"] == "MapCreator"))
        {
            string id = (string?)file["FileId"] ?? throw new InvalidDataException("Map has no identity.");
            if (maps.Any(m => m.Id == id))
                throw new InvalidDataException("Duplicate map identity in save index.");
            var save = file["Saves"]?.AsArray().OfType<JsonObject>().OrderByDescending(s => (string?)s["CreatedAt"], StringComparer.Ordinal).FirstOrDefault();
            if (save == null)
                continue;
            string path = Inside(Root, (string?)save["RelativePath"] ?? "");
            byte[] bytes = File.ReadAllBytes(path);
            string hash = Json.Hash(bytes);
            if (hash != Json.Hash(File.ReadAllBytes(path)))
                throw new IOException("A map changed during scanning. Refresh to retry.");
            maps.Add(new(id, (string?)file["FileName"] ?? id, (string?)file["AuthorDisplayName"] ?? "", path, hash));
        }
        if (Json.Hash(original) != Json.Hash(File.ReadAllBytes(ManifestPath)))
            throw new IOException("The game changed its save index during scanning. Refresh to retry.");
        return new(Json.Hash(original), manifest, modes)
        {
            Maps = maps
        };
    }
    void CheckClosed()
    {
        if ((running ?? Modes.GameRunning)())
            throw new InvalidOperationException("Close Splitgate before installing maps, applying modes or restoring backups.");
    }
    void CheckCurrent(GameSnapshot expected)
    {
        var current = Read();
        if (current.ManifestHash != expected.ManifestHash || !current.Modes.Select(m => (m.Id, m.ArchiveHash)).SequenceEqual(expected.Modes.Select(m => (m.Id, m.ArchiveHash))) || !current.Maps.Select(m => (m.Id, m.ArchiveHash)).SequenceEqual(expected.Maps.Select(m => (m.Id, m.ArchiveHash))))
            throw new IOException("Game saves changed since refresh. Refresh before saving; your draft is retained.");
    }
    static string Owner(JsonArray entries)
    {
        var local = entries.OfType<JsonObject>().Where(f => (string?)f["ContentType"] == "CustomModes" && !string.IsNullOrWhiteSpace((string?)f["OwnerId"])).ToArray();
        var owners = local.Select(f => (string)f["OwnerId"]!).Distinct().ToArray();
        if (owners.Length == 1)
            return owners[0];
        var probe = local.Where(f => (string?)f["FileName"] == "SpinGate Owner Check" || (string?)f["FileName"] == "SGL Owner Check").ToArray();
        if (probe.Length == 1)
            return (string)probe[0]["OwnerId"]!;
        throw new InvalidDataException("Save a custom mode named SGL Owner Check in-game on this PC, then close the game and refresh. This identifies your save owner.");
    }
    public string Apply(GameSnapshot expected, string? existingId, JsonObject payload)
    {
        CheckClosed();
        CheckCurrent(expected);
        var wrapped = Configuration.Wrapped(payload);
        Modes.Validate(wrapped);
        var doc = Configuration.Clone(expected.Manifest);
        var files = doc["Files"]!.AsArray();
        string name = Configuration.Name(payload).Trim();
        if (files.OfType<JsonObject>().Any(f => (string?)f["ContentType"] == "CustomModes" && (string?)f["FileId"] != existingId && string.Equals(((string?)f["FileName"])?.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("That mode name already exists. Open it to update, or choose a new name.");
        var old = files.OfType<JsonObject>().SingleOrDefault(f => (string?)f["ContentType"] == "CustomModes" && (string?)f["FileId"] == existingId);
        if (existingId != null && old == null)
            throw new IOException("The selected mode no longer exists. Refresh before saving.");
        string fileId = old == null ? Guid.NewGuid().ToString() : (string)old["FileId"]!;
        if (!Guid.TryParse(fileId, out _))
            throw new InvalidDataException("Unsupported file identity; duplicate this mode instead.");
        string saveId = Guid.NewGuid().ToString(), relative = $"CustomModes/{fileId}/{saveId}.bin", timestamp = DateTime.UtcNow.ToString("O");
        var entry = old == null ? new JsonObject { ["FileId"] = fileId, ["ContentType"] = "CustomModes", ["OwnerId"] = Owner(files), ["AuthorDisplayName"] = "", ["CreatedAt"] = timestamp } : Configuration.Clone(old);
        entry["FileName"] = name;
        entry["Saves"] = new JsonArray(new JsonObject { ["SaveId"] = saveId, ["RelativePath"] = relative, ["SaveType"] = "Manual", ["CreatedAt"] = timestamp });
        if (old != null)
            files[files.IndexOf(old)] = entry;
        else
            files.Add(entry);
        byte[] original = File.ReadAllBytes(ManifestPath);
        string backup = CreateBackup(original, expected, old == null ? [] : [existingId!]);
        string destination = Inside(Root, relative), temp = ManifestPath + "." + saveId + ".tmp";
        bool committed = false;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            File.WriteAllBytes(destination, Modes.Pack(wrapped));
            File.WriteAllText(temp, doc.ToJsonString(), EncodingOf(original));
            CheckClosed();
            CheckCurrent(expected);
            CommitManifest(temp, expected.ManifestHash, Path.Combine(backup, "manifest-before-replace.json"));
            committed = true;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); if (!committed && File.Exists(destination)) File.Delete(destination); }
        Json.Atomic(Path.Combine(backup, "operation.json"), new JsonObject { ["operation"] = old == null ? "install" : "update", ["modeId"] = fileId, ["name"] = name }.ToJsonString());
        return backup;
    }
    static Encoding EncodingOf(byte[] b) => b.Length >= 2 && b[0] == 255 && b[1] == 254 ? Encoding.Unicode : b.Length >= 2 && b[0] == 254 && b[1] == 255 ? Encoding.BigEndianUnicode : new UTF8Encoding(b.Length >= 3 && b[0] == 239 && b[1] == 187 && b[2] == 191);
    string CreateBackup(byte[] original, GameSnapshot snapshot, IEnumerable<string> ids)
    {
        string backup = Path.Combine(Root, "Backup", "SGL-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(backup);
        File.WriteAllBytes(Path.Combine(backup, "CloudSaveManifest.json"), original);
        // A full index restore must have every referenced archive available.
        foreach (var file in snapshot.Manifest["Files"]!.AsArray().OfType<JsonObject>())
            foreach (var save in file["Saves"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                string? relative = (string?)save["RelativePath"];
                if (relative == null)
                    continue;
                string source = Inside(Root, relative);
                if (!File.Exists(source))
                    throw new IOException("Referenced archive is missing; backup cannot be completed: " + relative);
                string target = Inside(Path.Combine(backup, "PreviousFiles"), relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, false);
            }
        return backup;
    }
    public string RestoreBackup(GameSnapshot expected, string backup)
    {
        CheckClosed();
        CheckCurrent(expected);
        string backupRoot = Path.Combine(Root, "Backup");
        backup = Inside(backupRoot, Path.GetRelativePath(backupRoot, backup));
        byte[] bytes = File.ReadAllBytes(Path.Combine(backup, "CloudSaveManifest.json"));
        JsonObject doc;
        using (var reader = new StreamReader(new MemoryStream(bytes), true))
            doc = (JsonObject)Json.Read(reader.ReadToEnd());
        var copies = new List<(string Source, string Destination)>();
        foreach (var file in doc["Files"]!.AsArray().OfType<JsonObject>())
            foreach (var save in file["Saves"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                string relative = (string)save["RelativePath"]!;
                string source = Inside(Path.Combine(backup, "PreviousFiles"), relative);
                if (!File.Exists(source))
                    throw new IOException("This backup lacks an archive snapshot: " + relative + ". Index-only legacy backups require manual recovery.");
                string newId = Guid.NewGuid().ToString();
                string fresh = Path.Combine(Path.GetDirectoryName(relative) ?? "", newId + Path.GetExtension(relative)).Replace('\\', '/');
                copies.Add((source, Inside(Root, fresh)));
                save["SaveId"] = newId;
                save["RelativePath"] = fresh;
            }
        string safety = CreateBackup(File.ReadAllBytes(ManifestPath), expected, []);
        string temp = ManifestPath + ".restore-" + Guid.NewGuid().ToString("N");
        var created = new List<string>();
        bool committed = false;
        try
        {
            // Restore to fresh paths, so archives overwritten in-place by the game
            // are recovered without changing any currently registered archive.
            foreach (var copy in copies)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(copy.Destination)!);
                File.Copy(copy.Source, copy.Destination, false);
                created.Add(copy.Destination);
            }
            File.WriteAllText(temp, doc.ToJsonString(), EncodingOf(bytes));
            CheckClosed();
            CheckCurrent(expected);
            CommitManifest(temp, expected.ManifestHash, Path.Combine(safety, "manifest-before-replace.json"));
            committed = true;
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
            if (!committed)
                foreach (string path in created)
                    File.Delete(path);
        }
        return safety;
    }
    void CommitManifest(string temp, string expectedHash, string backup)
    {
        using var held = new FileStream(ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var bytes = new MemoryStream();
        held.CopyTo(bytes);
        if (Json.Hash(bytes.ToArray()) != expectedHash)
            throw new IOException("Save index changed during the transaction. Nothing was registered.");
        File.Replace(temp, ManifestPath, backup);
    }
}
