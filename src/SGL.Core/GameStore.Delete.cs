using System.Text.Json.Nodes;
namespace SGL.Core;

public sealed record DeletionResult(string BackupDirectory, IReadOnlyList<string> CleanupIssues);

public sealed partial class GameStore
{
    public DeletionResult Delete(GameSnapshot expected, string contentType, string fileId)
        => DeleteMany(expected, contentType, [fileId]);

    public DeletionResult DeleteMany(GameSnapshot expected, string contentType, IEnumerable<string> fileIds)
    {
        CheckClosed();
        CheckCurrent(expected);
        var ids = fileIds.Distinct(StringComparer.Ordinal).ToArray();
        if (contentType is not ("CustomModes" or "MapCreator") || ids.Length == 0 || ids.Any(id => !Maps.ValidId(id)))
            throw new InvalidDataException("Choose valid installed maps or gamemodes to delete.");
        var doc = Configuration.Clone(expected.Manifest);
        var entries = doc["Files"]!.AsArray();
        var selected = ids.Select(id => entries.OfType<JsonObject>().SingleOrDefault(f => (string?)f["ContentType"] == contentType && (string?)f["FileId"] == id)
            ?? throw new IOException("A selected item no longer exists. Refresh before deleting.")).ToArray();
        string contentRoot = Inside(Root, contentType);
        void CheckPath(string path)
        {
            if (!path.StartsWith(contentRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A save path is outside this content folder; nothing was deleted.");
            for (string? current = path; current != null && !current.Equals(Root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("Deletion cannot follow a linked file or folder; nothing was deleted.");
        }
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in selected)
        {
            foreach (var save in entry["Saves"]?.AsArray().OfType<JsonObject>() ?? [])
                targets.Add(Inside(Root, (string?)save["RelativePath"] ?? ""));
            // Include old/unregistered saves, so a folder scan cannot resurrect an item.
            string fileId = (string)entry["FileId"]!;
            string flat = Inside(Root, contentType + "/" + fileId + ".bin"), native = Inside(Root, contentType + "/" + fileId);
            CheckPath(flat);
            CheckPath(native);
            if (File.Exists(flat)) targets.Add(flat);
            if (Directory.Exists(native))
                foreach (string path in Directory.GetFiles(native, "*.bin", SearchOption.TopDirectoryOnly))
                    targets.Add(path);
        }
        foreach (string path in targets)
            CheckPath(path);
        var shared = entries.OfType<JsonObject>().Where(f => !selected.Contains(f)).SelectMany(f => f["Saves"]?.AsArray().OfType<JsonObject>() ?? []).Select(s => Inside(Root, (string?)s["RelativePath"] ?? "")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (targets.Any(shared.Contains))
            throw new InvalidDataException("Another installed item references one of these files; nothing was deleted.");
        var hashes = targets.ToDictionary(path => path, path => Json.Hash(File.ReadAllBytes(path)), StringComparer.OrdinalIgnoreCase);
        string backup = CreateBackup(File.ReadAllBytes(ManifestPath), expected, ids);
        var operation = new JsonObject
        {
            ["operation"] = "delete",
            ["contentType"] = contentType,
            ["fileId"] = ids.Length == 1 ? ids[0] : null,
            ["name"] = selected.Length == 1 ? (string?)selected[0]["FileName"] : null,
            ["fileIds"] = new JsonArray(ids.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()),
            ["names"] = new JsonArray(selected.Select(f => f["FileName"]?.DeepClone()).ToArray()),
            ["state"] = "prepared",
            ["files"] = new JsonArray(targets.Select(path => (JsonNode)JsonValue.Create(Path.GetRelativePath(Root, path).Replace('\\', '/'))!).ToArray())
        };
        string journal = Path.Combine(backup, "operation.json");
        Json.Atomic(journal, operation.ToJsonString());
        foreach (var entry in selected) entries.Remove(entry);
        string temp = ManifestPath + ".delete-" + Guid.NewGuid().ToString("N");
        try
        {
            byte[] original = File.ReadAllBytes(ManifestPath);
            File.WriteAllText(temp, doc.ToJsonString(), EncodingOf(original));
            CheckClosed();
            CheckCurrent(expected);
            foreach (var pair in hashes)
                if (Json.Hash(File.ReadAllBytes(pair.Key)) != pair.Value)
                    throw new IOException("An item file changed before deletion. Refresh and retry; nothing was deleted.");
            CommitManifest(temp, expected.ManifestHash, Path.Combine(backup, "manifest-before-replace.json"));
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        // Commit the index first. An interrupted cleanup must never leave a live
        // manifest pointing to files that have already been moved away.
        var issues = new List<string>();
        foreach (var pair in hashes)
        {
            try
            {
                CheckClosed();
                CheckPath(pair.Key);
                if (Json.Hash(File.ReadAllBytes(pair.Key)) != pair.Value)
                    throw new IOException("File changed after deletion was registered; kept the newer file.");
                string target = Inside(Path.Combine(backup, "RemovedFiles"), Path.GetRelativePath(Root, pair.Key));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(pair.Key, target, false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                issues.Add(Path.GetRelativePath(Root, pair.Key) + ": " + error.Message);
            }
        }
        operation["state"] = issues.Count == 0 ? "complete" : "cleanup-pending";
        operation["cleanupIssues"] = new JsonArray(issues.Select(issue => (JsonNode)JsonValue.Create(issue)!).ToArray());
        try
        {
            Json.Atomic(journal, operation.ToJsonString());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { issues.Add("Could not update deletion receipt: " + error.Message); }
        return new(backup, issues);
    }
}
