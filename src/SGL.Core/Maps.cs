using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace SGL.Core;

public sealed record CommunityMap(string Id, string Name, string Author, string Category, string Download, string? Image)
{
    public override string ToString() => $"{Name} · {Author} · {Category}";
}
public sealed record SavedMap(string Id, string Name, string Author, string ArchivePath, string ArchiveHash)
{
    public override string ToString() => Name;
}
public sealed record MapArchive(string Name, string Author);

public static class Maps
{
    public const string CatalogUrl = "https://raw.githubusercontent.com/Splitgate-Architects/Splitgate-CommunityMaps/master/docs/maps.json";
    public const string Website = "https://splitgate-architects.github.io/Splitgate-CommunityMaps/";
    public const int MaximumArchive = 128_000_000;
    const string RawPrefix = "https://raw.githubusercontent.com/Splitgate-Architects/Splitgate-CommunityMaps/master/Maps/";
    const string CdnPrefix = "https://cdn.jsdelivr.net/gh/Splitgate-Architects/Splitgate-CommunityMaps@master/Maps/";
    public static bool ValidId(string value) => value.Length is > 0 and <= 180 && Regex.IsMatch(value, @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]*\z") && value is not "." and not ".." && !value.EndsWith('.') && !value.Split('.')[0].Equals("CON", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(value.Split('.')[0], @"\A(PRN|AUX|NUL|COM[0-9]|LPT[0-9])\z", RegexOptions.IgnoreCase);
    public static string OfficialAsset(string url, string extension)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Invalid community map URL.");
        string prefix = url.StartsWith(CdnPrefix, StringComparison.Ordinal) ? CdnPrefix : url.StartsWith(RawPrefix, StringComparison.Ordinal) ? RawPrefix : throw new InvalidDataException("Map asset is outside the community repository.");
        string path = Uri.UnescapeDataString(url[prefix.Length..]);
        if (path.Split('/').Any(p => p is "" or "." or ".." || p.Contains('\\') || p.Contains(':')) || !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid community map asset path.");
        // The raw repository avoids CDN file-size restrictions and stale CDN copies.
        return RawPrefix + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
    }
    public static CommunityMap[] ParseCatalog(string text)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(text) > 8_000_000)
            throw new InvalidDataException("Map catalog exceeds 8 MB.");
        var list = Json.Read(text).AsArray();
        if (list.Count > 20_000)
            throw new InvalidDataException("Map catalog has too many entries.");
        var maps = new List<CommunityMap>();
        foreach (var node in list)
        {
            string id = (string?)node?["id"] ?? "", name = (string?)node?["name"] ?? "";
            if (!ValidId(id) || string.IsNullOrWhiteSpace(name) || name.Length > 300 || name.Any(char.IsControl))
                throw new InvalidDataException("Invalid community map identity or name.");
            string download = OfficialAsset((string?)node?["download"] ?? "", ".bin");
            if (!Uri.UnescapeDataString(new Uri(download).Segments.Last()).Equals(id + ".bin", StringComparison.Ordinal))
                throw new InvalidDataException("Map download does not match its catalog identity.");
            string? image = (string?)node?["image"];
            if (!string.IsNullOrWhiteSpace(image))
            {
                // Image previews are optional; an unsupported image must not prevent installation.
                try
                {
                    image = OfficialAsset(image, Path.GetExtension(new Uri(image).AbsolutePath));
                }
                catch { image = null; }
            }
            maps.Add(new(id, name, (string?)node?["author"] ?? "Unknown", (string?)node?["category"] ?? "Unsorted", download, image));
        }
        if (maps.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != maps.Count)
            throw new InvalidDataException("Duplicate map identity in catalog.");
        return maps.ToArray();
    }
    static HttpClient Client(HttpMessageHandler? handler)
    {
        var client = handler == null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler, false);
        client.Timeout = TimeSpan.FromMinutes(3);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SGL/0.2.0");
        return client;
    }
    static async Task<byte[]> Fetch(HttpClient client, string url, int limit, CancellationToken cancel, IProgress<double>? progress = null)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > limit)
            throw new InvalidDataException("Map download has an invalid status or exceeds its size limit.");
        using var input = await response.Content.ReadAsStreamAsync(cancel);
        using var output = new MemoryStream();
        byte[] buffer = new byte[65_536];
        int read;
        while ((read = await input.ReadAsync(buffer, cancel)) > 0)
        {
            if (output.Length + read > limit)
                throw new InvalidDataException("Map download exceeds its size limit.");
            output.Write(buffer, 0, read);
            if (response.Content.Headers.ContentLength is long total && total > 0)
                progress?.Report(output.Length * 100d / total);
        }
        if (response.Content.Headers.ContentLength is long expected && output.Length != expected)
            throw new InvalidDataException("Map download is incomplete.");
        return output.ToArray();
    }
    public static async Task<CommunityMap[]> Catalog(string cache, bool refresh = false, CancellationToken cancel = default, HttpMessageHandler? handler = null)
    {
        if (!refresh && File.Exists(cache))
            return ParseCatalog(await File.ReadAllTextAsync(cache, cancel));
        using var client = Client(handler);
        var bytes = await Fetch(client, CatalogUrl, 8_000_000, cancel);
        string text = System.Text.Encoding.UTF8.GetString(bytes);
        var result = ParseCatalog(text);
        Json.Atomic(cache, text);
        return result;
    }
    public static async Task<byte[]> Download(CommunityMap map, CancellationToken cancel = default, IProgress<double>? progress = null, HttpMessageHandler? handler = null)
    {
        using var client = Client(handler);
        var bytes = await Fetch(client, OfficialAsset(map.Download, ".bin"), MaximumArchive, cancel, progress);
        Inspect(bytes);
        return bytes;
    }
    public static async Task<byte[]?> Preview(CommunityMap map, CancellationToken cancel = default)
    {
        if (string.IsNullOrEmpty(map.Image))
            return null;
        string extension = Path.GetExtension(new Uri(map.Image).AbsolutePath);
        if (extension.ToLowerInvariant() is not (".jpg" or ".jpeg" or ".png"))
            return null;
        using var client = Client(null);
        return await Fetch(client, OfficialAsset(map.Image, extension), 8_000_000, cancel);
    }
    public static byte[]? InstalledPreview(string archivePath)
    {
        // Read only the image entry, so previews also work offline and reflect Map Lab edits.
        using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        if (zip.Entries.Count > 1024) throw new InvalidDataException("Map archive has too many entries.");
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals("Screenshot.jpg", StringComparison.OrdinalIgnoreCase))
            ?? zip.Entries.FirstOrDefault(e => e.FullName.Equals("Screenshot.jpeg", StringComparison.OrdinalIgnoreCase) || e.FullName.Equals("Screenshot.png", StringComparison.OrdinalIgnoreCase));
        if (entry == null || entry.Length == 0) return null;
        if (entry.Length > 8_000_000) throw new InvalidDataException("Map screenshot exceeds 8 MB.");
        using var input = entry.Open();
        using var output = new MemoryStream();
        byte[] buffer = new byte[65_536];
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            if (output.Length + count > 8_000_000) throw new InvalidDataException("Map screenshot exceeds 8 MB.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    public static MapArchive Inspect(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes.Length > MaximumArchive || bytes[0] != 'P' || bytes[1] != 'K')
            throw new InvalidDataException("Expected a Splitgate map .bin ZIP archive.");
        using var stream = new MemoryStream(bytes, false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > 1024 || zip.Entries.Sum(e => e.Length) > 1_000_000_000)
            throw new InvalidDataException("Map archive exceeds expansion limits.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.Contains('\\') || entry.FullName.StartsWith('/') || entry.FullName.Split('/').Any(p => p is "." or ".." || p.Contains(':')) || !names.Add(entry.FullName))
                throw new InvalidDataException("Map archive has an unsafe or duplicate entry.");
        }
        if (zip.GetEntry("World.cf1047") is not { Length: > 0 })
            throw new InvalidDataException("Archive has no World.cf1047 map data. Prefabs and mode archives are not maps.");
        if (zip.GetEntry("info.json") is not { } info)
            return new("Imported map", "Unknown");
        if (info.Length > 65_536)
            throw new InvalidDataException("Map metadata exceeds 64 KB.");
        using var reader = new StreamReader(info.Open(), detectEncodingFromByteOrderMarks: true);
        var metadata = Json.Read(reader.ReadToEnd()).AsObject();
        return new((string?)metadata["name"] ?? "Imported map", (string?)metadata["author"] ?? "Unknown");
    }
}

public sealed class MapLinks(string path)
{
    JsonObject Read() => File.Exists(path) ? Json.Read(File.ReadAllText(path)).AsObject() : new();
    public string[] ModesFor(string mapId) => Read()[mapId]?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? [];
    public void Set(string mapId, string modeId, bool associated)
    {
        var doc = Read();
        var ids = ModesFor(mapId).ToHashSet(StringComparer.Ordinal);
        if (associated)
            ids.Add(modeId);
        else
            ids.Remove(modeId);
        if (ids.Count == 0)
            doc.Remove(mapId);
        else
            doc[mapId] = new JsonArray(ids.Order().Select(id => (JsonNode)JsonValue.Create(id)!).ToArray());
        Json.Atomic(path, Json.Canonical(doc));
    }
}
