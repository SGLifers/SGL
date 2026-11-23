using System.Text.Json.Nodes;
namespace SGL.Core;

public sealed class LibraryState(string path)
{
    JsonObject Read() => File.Exists(path) ? Json.Read(File.ReadAllText(path)).AsObject() : new();
    static string Key(string service, string item) => service.TrimEnd('/') + "/" + item;
    public JsonObject? Installed(string service, string item) => Read()["installed"]?[Key(service, item)]?.AsObject();
    public JsonObject? Published(string service, string kind, string localId) => Read()["published"]?[Key(service, kind + "/" + localId)]?.AsObject();
    public JsonObject? OriginForLocal(string service, string kind, string localId) => Read()["installed"]?.AsObject().Where(p => p.Key.StartsWith(service.TrimEnd('/') + "/", StringComparison.Ordinal)).Select(p => p.Value!.AsObject()).FirstOrDefault(r => (string?)r["kind"] == kind && (string?)r["localFileId"] == localId);
    public void RecordInstall(string service, IEnumerable<JsonObject> records)
    {
        var doc = Read();
        if (doc["installed"] is not JsonObject)
            doc["installed"] = new JsonObject();
        foreach (var record in records)
            doc["installed"]![Key(service, (string)record["itemId"]!)] = record.DeepClone();
        Json.Atomic(path, Json.Canonical(doc));
    }
    public void RecordPublication(string service, string kind, string localId, string itemId, string revisionId, string contentHash)
    {
        var doc = Read();
        if (doc["published"] is not JsonObject)
            doc["published"] = new JsonObject();
        doc["published"]![Key(service, kind + "/" + localId)] = new JsonObject { ["itemId"] = itemId, ["revisionId"] = revisionId, ["contentHash"] = contentHash };
        Json.Atomic(path, Json.Canonical(doc));
    }
    public bool HasLocalChanges(GameSnapshot snapshot, string service, SharedPackage package)
    {
        foreach (var entry in package.Entries)
        {
            var record = Installed(service, entry.ItemId);
            string? id = (string?)record?["localFileId"];
            string? hash = entry.Kind == "mode" ? snapshot.Modes.FirstOrDefault(m => m.Id == id)?.ArchiveHash : snapshot.Maps.FirstOrDefault(m => m.Id == id)?.ArchiveHash;
            if (hash != null && hash != (string?)record?["archiveHash"])
                return true;
        }
        return false;
    }
}
