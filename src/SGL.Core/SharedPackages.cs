using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
namespace SGL.Core;

public static class SharedPackages
{
    public static void Validate(SharedPackage package)
    {
        if (package.Entries.Count is < 1 or > 2 || package.Entries.Select(e => e.Kind).Distinct().Count() != package.Entries.Count)
            throw new InvalidDataException("A package contains a map, a mode, or one of each.");
        foreach (var item in package.Entries)
        {
            _ = SharedTags.Read(item.Attribution);
            if (!LibraryClient.ValidId(item.ItemId) || !LibraryClient.ValidId(item.RevisionId) || item.Kind is not ("map" or "mode"))
                throw new InvalidDataException("Invalid shared package identity.");
            if (item.Kind == "map")
                Maps.Inspect(item.Data);
            else
            {
                if (item.Data.Length > 1_048_576)
                    throw new InvalidDataException("Mode exceeds 1 MB.");
                Modes.Validate(Configuration.Wrapped(Json.Read(Encoding.UTF8.GetString(item.Data)).AsObject()));
            }
        }
    }
    public static byte[] Pack(SharedPackage package)
    {
        Validate(package);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            var entries = new JsonArray();
            foreach (var item in package.Entries)
            {
                if (!LibraryClient.ValidId(item.ItemId) || !LibraryClient.ValidId(item.RevisionId) || item.Kind is not ("map" or "mode"))
                    throw new InvalidDataException("Invalid shared package identity.");
                string path = item.Kind + "/" + item.ItemId + (item.Kind == "map" ? ".bin" : ".json");
                using (var content = zip.CreateEntry(path, CompressionLevel.Fastest).Open())
                    content.Write(item.Data);
                entries.Add(new JsonObject { ["itemId"] = item.ItemId, ["revisionId"] = item.RevisionId, ["kind"] = item.Kind, ["title"] = item.Title, ["path"] = path, ["size"] = item.Data.Length, ["sha256"] = Json.Hash(item.Data), ["attribution"] = item.Attribution.DeepClone() });
            }
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open(), new UTF8Encoding(false));
            writer.Write(new JsonObject { ["format"] = "sgl-package", ["version"] = 1, ["title"] = package.Title, ["bundleId"] = package.BundleId, ["bundleRevisionId"] = package.BundleRevisionId, ["entries"] = entries }.ToJsonString());
        }
        return output.ToArray();
    }
    public static SharedPackage Read(byte[] data)
    {
        if (data.Length > 130_000_000)
            throw new InvalidDataException("Shared package exceeds 130 MB.");
        using var stream = new MemoryStream(data, false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count is < 2 or > 3 || zip.Entries.Sum(e => e.Length) > 130_000_000 || zip.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zip.Entries.Count)
            throw new InvalidDataException("Invalid package entries or expansion size.");
        var metadata = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("Missing package manifest.");
        if (metadata.Length > 65_536)
            throw new InvalidDataException("Package manifest exceeds 64 KB.");
        JsonObject manifest;
        using (var reader = new StreamReader(metadata.Open()))
            manifest = Json.Read(reader.ReadToEnd()).AsObject();
        if ((string?)manifest["format"] != "sgl-package" || (int?)manifest["version"] != 1)
            throw new InvalidDataException("Unsupported shared package version.");
        var entries = new List<SharedEntry>();
        foreach (var node in manifest["entries"]!.AsArray())
        {
            string id = (string)node!["itemId"]!, revision = (string)node["revisionId"]!, kind = (string)node["kind"]!;
            string expectedPath = kind + "/" + id + (kind == "map" ? ".bin" : ".json");
            if (!LibraryClient.ValidId(id) || !LibraryClient.ValidId(revision) || kind is not ("map" or "mode") || (string?)node["path"] != expectedPath)
                throw new InvalidDataException("Invalid package file identity or path.");
            var entry = zip.GetEntry(expectedPath) ?? throw new InvalidDataException("Missing package file.");
            if (entry.Length != (long)node["size"]! || entry.Length > (kind == "map" ? Maps.MaximumArchive : 1_048_576))
                throw new InvalidDataException("Invalid package file size.");
            using var content = entry.Open();
            using var bytes = new MemoryStream();
            content.CopyTo(bytes);
            var value = bytes.ToArray();
            if (!Json.Hash(value).Equals((string?)node["sha256"], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Package checksum did not match.");
            if (kind == "map")
                Maps.Inspect(value);
            else
                Modes.Validate(Configuration.Wrapped(Json.Read(Encoding.UTF8.GetString(value)).AsObject()));
            entries.Add(new(id, revision, kind, (string)node["title"]!, value, node["attribution"]?.AsObject() ?? new()));
        }
        if (entries.Count is < 1 or > 2 || entries.Select(e => e.Kind).Distinct().Count() != entries.Count || zip.Entries.Count != entries.Count + 1)
            throw new InvalidDataException("A package contains one map and/or one mode.");
        var package = new SharedPackage((string)manifest["title"]!, entries, (string?)manifest["bundleId"], (string?)manifest["bundleRevisionId"]);
        Validate(package);
        return package;
    }
}
