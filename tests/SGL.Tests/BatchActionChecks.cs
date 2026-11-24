using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using SGL.Core;

static class BatchActionChecks
{
    public static void Run(Action<bool, string> check)
    {
        void Reject(Action action, string message)
        {
            bool rejected = false;
            try { action(); } catch { rejected = true; }
            check(rejected, message);
        }
        foreach (string kind in new[] { "CustomModes", "MapCreator" })
        {
            var store = Fixture();
            var before = store.Read();
            string[] ids = before.Manifest["Files"]!.AsArray().OfType<JsonObject>().Where(f => (string?)f["ContentType"] == kind).Take(2).Select(f => (string)f["FileId"]!).ToArray();
            var files = RegisteredFiles(store, before);
            byte[] index = File.ReadAllBytes(store.ManifestPath);
            Reject(() => store.DeleteMany(before, kind, []), "Empty batch deletion accepted");
            Reject(() => store.DeleteMany(before, kind, [ids[0], "missing-item"]), "Partially missing batch accepted");
            Reject(() => store.DeleteMany(before, kind, [ids[0], "../outside"]), "Batch accepted invalid last identity");
            Reject(() => new GameStore(store.Root, () => true).DeleteMany(before, kind, ids), "Batch deletion ignored running game");
            int guards = 0;
            Reject(() => new GameStore(store.Root, () => ++guards > 1).DeleteMany(before, kind, ids), "Batch committed after game started");
            check(File.ReadAllBytes(store.ManifestPath).SequenceEqual(index) && files.All(f => File.ReadAllBytes(f.Key).SequenceEqual(f.Value)), "Rejected batch partially deleted files");
            int backups = Directory.GetDirectories(Path.Combine(store.Root, "Backup")).Length;
            var result = store.DeleteMany(before, kind, [ids[0], ids[1], ids[0]]);
            var after = store.Read();
            check(Directory.GetDirectories(Path.Combine(store.Root, "Backup")).Length == backups + 1, "Batch did not create exactly one backup");
            check(result.CleanupIssues.Count == 0, "Batch cleanup failed");
            check(after.Modes.Count == (kind == "CustomModes" ? 1 : 3) && after.Maps.Count == (kind == "MapCreator" ? 1 : 3), "Batch changed unselected content");
            check(after.Manifest["Preserve"]!.GetValue<string>() == "metadata" && File.ReadAllBytes(store.ManifestPath)[0] == 255, "Batch lost metadata or index encoding");
            check(!after.Manifest["Files"]!.AsArray().Any(f => ids.Contains((string)f!["FileId"]!)), "A selected batch item remained registered");
            check(RegisteredFiles(store, after).All(f => files[f.Key].SequenceEqual(f.Value)), "Batch changed an unselected archive");
            var receipt = Json.Read(File.ReadAllText(Path.Combine(result.BackupDirectory, "operation.json")));
            check(receipt["fileIds"]!.AsArray().Count == 2 && (string?)receipt["state"] == "complete", "Batch receipt lost identities or cleanup state");
            store.RestoreBackup(after, result.BackupDirectory);
            var restored = store.Read();
            check(restored.Modes.Select(m => m.ArchiveHash).SequenceEqual(before.Modes.Select(m => m.ArchiveHash)) && restored.Maps.Select(m => m.ArchiveHash).SequenceEqual(before.Maps.Select(m => m.ArchiveHash)), "Batch backup failed to restore all selected and unselected content");
        }
        foreach (bool unsafePath in new[] { false, true })
        {
            var store = Fixture();
            var before = store.Read();
            var doc = Configuration.Clone(before.Manifest);
            var maps = doc["Files"]!.AsArray().OfType<JsonObject>().Where(f => (string?)f["ContentType"] == "MapCreator").ToArray();
            if (unsafePath) maps[1]["Saves"]![0]!["RelativePath"] = before.Modes[0].ArchivePath.Replace(store.Root + Path.DirectorySeparatorChar, "");
            else maps[2]["Saves"]![0]!["RelativePath"] = maps[1]["Saves"]![0]!["RelativePath"]!.DeepClone();
            File.WriteAllText(store.ManifestPath, doc.ToJsonString(), Encoding.Unicode);
            var expected = store.Read();
            byte[] original = File.ReadAllBytes(store.ManifestPath);
            Reject(() => store.DeleteMany(expected, "MapCreator", before.Maps.Take(2).Select(m => m.Id)), "Batch allowed shared or unsafe last archive");
            check(File.ReadAllBytes(store.ManifestPath).SequenceEqual(original) && File.Exists(before.Maps[0].ArchivePath), "Invalid last archive partially deleted the first item");
        }
        var locked = Fixture();
        var lockedBefore = locked.Read();
        using (var held = new FileStream(lockedBefore.Maps[1].ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = locked.DeleteMany(lockedBefore, "MapCreator", lockedBefore.Maps.Take(2).Select(m => m.Id));
            check(result.CleanupIssues.Count == 1 && locked.Read().Maps.Count == 1, "Partial batch cleanup was reported as an uncommitted deletion");
            check(File.Exists(lockedBefore.Maps[1].ArchivePath) && !File.Exists(lockedBefore.Maps[0].ArchivePath), "Locked batch cleanup handled wrong files");
            check(File.Exists(GameStore.Inside(Path.Combine(result.BackupDirectory, "PreviousFiles"), Path.GetRelativePath(locked.Root, lockedBefore.Maps[1].ArchivePath))), "Locked batch archive was not backed up");
        }
    }
    static Dictionary<string, byte[]> RegisteredFiles(GameStore store, GameSnapshot snapshot) => snapshot.Manifest["Files"]!.AsArray().OfType<JsonObject>()
        .SelectMany(f => f["Saves"]!.AsArray()).Select(s => GameStore.Inside(store.Root, (string)s!["RelativePath"]!))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    static GameStore Fixture()
    {
        string root = Path.Combine(Path.GetTempPath(), "sgl-batch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var files = new JsonArray();
        foreach (string kind in new[] { "CustomModes", "MapCreator" })
            for (int i = 0; i < 3; i++)
            {
                string id = Guid.NewGuid().ToString(), relative = kind + "/" + id + "/save.bin";
                string path = GameStore.Inside(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                byte[] bytes;
                if (kind == "CustomModes")
                {
                    var p = Configuration.New("tdm");
                    p["Options"]!["CustomName"] = "Mode " + i;
                    bytes = Modes.Pack(Configuration.Wrapped(p));
                }
                else
                {
                    using var stream = new MemoryStream();
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                    using (var writer = new StreamWriter(zip.CreateEntry("World.cf1047").Open())) writer.Write("Map " + i);
                    bytes = stream.ToArray();
                }
                File.WriteAllBytes(path, bytes);
                files.Add(new JsonObject { ["ContentType"] = kind, ["FileId"] = id, ["FileName"] = kind + i, ["OwnerId"] = "fixture-owner", ["Saves"] = new JsonArray(new JsonObject { ["RelativePath"] = relative, ["SaveId"] = "save", ["CreatedAt"] = "2026-01-01T00:00:00Z" }) });
            }
        File.WriteAllText(Path.Combine(root, "CloudSaveManifest.json"), new JsonObject { ["Preserve"] = "metadata", ["Files"] = files }.ToJsonString(), Encoding.Unicode);
        return new GameStore(root, () => false);
    }
}
