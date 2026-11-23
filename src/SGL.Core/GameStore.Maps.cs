using System.Text.Json.Nodes;
namespace SGL.Core;

public sealed partial class GameStore
{
    public string InstallMap(GameSnapshot expected, string mapId, byte[] bytes, string? name = null, string? author = null)
    {
        CheckClosed();
        CheckCurrent(expected);
        if (!Maps.ValidId(mapId))
            throw new InvalidDataException("Invalid map identity.");
        var info = Maps.Inspect(bytes);
        var doc = Configuration.Clone(expected.Manifest);
        var files = doc["Files"]!.AsArray();
        var old = files.OfType<JsonObject>().SingleOrDefault(f => (string?)f["ContentType"] == "MapCreator" && (string?)f["FileId"] == mapId);
        if (old != null)
            throw new InvalidOperationException("This map is already installed. Its existing saves and Map Lab edits have been kept.");
        string relative = "MapCreator/" + mapId + ".bin", destination = Inside(Root, relative);
        if (File.Exists(destination))
            throw new IOException("An unregistered map file already uses this identity. It has been kept; import it with MapLoader or choose another identity.");
        string owner;
        var nativeOwners = files.OfType<JsonObject>().Where(f => (string?)f["ContentType"] == "MapCreator" && f["Saves"]?.AsArray().OfType<JsonObject>().Any(s => (string?)s["SaveId"] != "custom-map") == true).Select(f => (string?)f["OwnerId"]).Where(o => !string.IsNullOrWhiteSpace(o)).Distinct().ToArray();
        try
        {
            owner = Owner(files);
        }
        catch (InvalidDataException) when (nativeOwners.Length == 1) { owner = nativeOwners[0]!; }
        string timestamp = DateTime.UtcNow.ToString("O");
        // Match MapLoader's flat map registration, including its community save marker.
        files.Add(new JsonObject
        {
            ["FileId"] = mapId,
            ["FileName"] = "[Community] " + (name ?? info.Name),
            ["ContentType"] = "MapCreator",
            ["OwnerId"] = owner,
            ["AuthorDisplayName"] = author ?? info.Author,
            ["CreatedAt"] = timestamp,
            ["Saves"] = new JsonArray(new JsonObject { ["SaveId"] = "custom-map", ["RelativePath"] = relative, ["SaveType"] = "Manual", ["CreatedAt"] = timestamp })
        });
        byte[] original = File.ReadAllBytes(ManifestPath);
        string backup = CreateBackup(original, expected, []), temp = ManifestPath + ".map-" + Guid.NewGuid().ToString("N");
        bool created = false, committed = false;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                output.Write(bytes);
                output.Flush(true);
            }
            File.WriteAllText(temp, doc.ToJsonString(), EncodingOf(original));
            CheckClosed();
            CheckCurrent(expected);
            CommitManifest(temp, expected.ManifestHash, Path.Combine(backup, "manifest-before-replace.json"));
            committed = true;
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
            if (!committed && created)
                File.Delete(destination);
        }
        Json.Atomic(Path.Combine(backup, "operation.json"), new JsonObject { ["operation"] = "install-map", ["mapId"] = mapId, ["name"] = name ?? info.Name, ["sha256"] = Json.Hash(bytes) }.ToJsonString());
        return backup;
    }
}
