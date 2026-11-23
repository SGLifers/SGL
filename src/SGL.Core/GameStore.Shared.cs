using System.Text;
using System.Text.Json.Nodes;
namespace SGL.Core;

public sealed record SharedInstallResult(string BackupDirectory, IReadOnlyList<JsonObject> Records);
public sealed partial class GameStore
{
    public SharedInstallResult InstallShared(GameSnapshot expected, SharedPackage package, LibraryState state, string service, bool copyLocalEdits = false)
    {
        CheckClosed();
        CheckCurrent(expected);
        // Apply the same limits and validation to online and portable content.
        SharedPackages.Validate(package);
        if (state.HasLocalChanges(expected, service, package) && !copyLocalEdits)
            throw new IOException("An installed item has local edits. Install a separate copy to keep those edits.");
        var doc = Configuration.Clone(expected.Manifest);
        var files = doc["Files"]!.AsArray();
        var writes = new List<(string Path, byte[] Data)>();
        var records = new List<JsonObject>();
        foreach (var entry in package.Entries)
        {
            var tracked = state.Installed(service, entry.ItemId);
            string contentType = entry.Kind == "mode" ? "CustomModes" : "MapCreator";
            string? localId = (string?)tracked?["localFileId"];
            var old = files.OfType<JsonObject>().SingleOrDefault(f => (string?)f["FileId"] == localId && (string?)f["ContentType"] == contentType);
            string? currentHash = entry.Kind == "mode" ? expected.Modes.FirstOrDefault(m => m.Id == localId)?.ArchiveHash : expected.Maps.FirstOrDefault(m => m.Id == localId)?.ArchiveHash;
            bool changed = old != null && currentHash != (string?)tracked?["archiveHash"];
            if (tracked != null && (string?)tracked["revisionId"] == entry.RevisionId && !(Json.Hash(entry.Data).Equals((string?)tracked["contentHash"], StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("This revision's content changed. Its previous installation was kept.");
            if (changed || !package.FromService && old != null && (string?)tracked?["revisionId"] != entry.RevisionId)
                old = null;
            if (old != null && (string?)tracked?["revisionId"] == entry.RevisionId && currentHash == (string?)tracked?["archiveHash"])
            {
                records.Add(Configuration.Clone(tracked!));
                continue;
            }
            string id = old == null ? Guid.NewGuid().ToString() : (string)old["FileId"]!;
            if (!Maps.ValidId(id))
                throw new InvalidDataException("Unsupported local file identity.");
            string saveId = Guid.NewGuid().ToString(), relative = $"{contentType}/{id}/{saveId}.bin", timestamp = DateTime.UtcNow.ToString("O");
            string name = entry.Title;
            byte[] bytes;
            if (entry.Kind == "mode")
            {
                var payload = Json.Read(Encoding.UTF8.GetString(entry.Data)).AsObject();
                name = Configuration.Name(payload);
                string original = name;
                int number = 1;
                while (files.OfType<JsonObject>().Any(f => (string?)f["ContentType"] == contentType && (string?)f["FileId"] != id && string.Equals((string?)f["FileName"], name, StringComparison.OrdinalIgnoreCase)))
                {
                    string suffix = " (Shared " + number++ + ")";
                    name = original[..Math.Min(original.Length, 80 - suffix.Length)] + suffix;
                }
                payload["Options"]!["CustomName"] = name;
                bytes = Modes.Pack(Configuration.Wrapped(payload));
            }
            else
            {
                Maps.Inspect(entry.Data);
                bytes = entry.Data;
                if (changed)
                    name += " (Shared copy)";
            }
            string owner;
            if (old != null)
                owner = (string)old["OwnerId"]!;
            else
                owner = Owner(files);
            var registration = old == null ? new JsonObject { ["FileId"] = id, ["ContentType"] = contentType, ["OwnerId"] = owner, ["CreatedAt"] = timestamp, ["AuthorDisplayName"] = (string?)entry.Attribution["item"]?["owner"]?["displayName"] ?? "" } : Configuration.Clone(old);
            registration["FileName"] = name;
            registration["Saves"] = new JsonArray(new JsonObject { ["SaveId"] = saveId, ["RelativePath"] = relative, ["SaveType"] = "Manual", ["CreatedAt"] = timestamp });
            if (old == null)
                files.Add(registration);
            else
                files[files.IndexOf(old)] = registration;
            writes.Add((Inside(Root, relative), bytes));
            records.Add(new JsonObject { ["itemId"] = entry.ItemId, ["revisionId"] = entry.RevisionId, ["kind"] = entry.Kind, ["localFileId"] = id, ["archiveHash"] = Json.Hash(bytes), ["contentHash"] = Json.Hash(entry.Data), ["fromService"] = package.FromService, ["attribution"] = entry.Attribution.DeepClone() });
        }
        if (writes.Count == 0)
            return new("", records);
        string backup = CreateBackup(File.ReadAllBytes(ManifestPath), expected, []);
        string temp = ManifestPath + ".shared-" + Guid.NewGuid().ToString("N");
        bool committed = false;
        var created = new List<string>();
        try
        {
            foreach (var write in writes)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(write.Path)!);
                using var output = new FileStream(write.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                created.Add(write.Path);
                output.Write(write.Data);
                output.Flush(true);
            }
            File.WriteAllText(temp, doc.ToJsonString(), EncodingOf(File.ReadAllBytes(ManifestPath)));
            // Retain recovery metadata before committing the game index.
            Json.Atomic(Path.Combine(backup, "shared-install.json"), new JsonObject { ["service"] = service, ["title"] = package.Title, ["records"] = new JsonArray(records.Select(r => (JsonNode)r.DeepClone()).ToArray()) }.ToJsonString());
            CheckClosed();
            CheckCurrent(expected);
            CommitManifest(temp, expected.ManifestHash, Path.Combine(backup, "manifest-before-replace.json"));
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
        return new(backup, records);
    }
}
