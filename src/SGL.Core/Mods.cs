using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace SGL.Core;

public sealed record CommunityMod(string Id, string Name, string Description, string Scope);
public sealed record ModRelease(string Version, string Download, string Sha256, CommunityMod[] Mods);
public sealed record ModBundle(ModRelease Release, IReadOnlyDictionary<string, byte[]> Files);

public static class Mods
{
    public const string Repository = "Splitgate-Architects/Splitgate-Mods";
    public const string Latest = "https://api.github.com/repos/" + Repository + "/releases/latest";
    public const string BootstrapUrl = "https://github.com/" + Repository + "/releases/download/v1.1.0/Splitgate-Mods.zip";
    public const string BootstrapHash = "163b79f7acd684cf6d99bfb454e47ebe09255dd5ce73ab39971a765e1a90a53e";
    public const string RuntimeUrl = "https://github.com/UE4SS-RE/RE-UE4SS/releases/download/v4.0.0/UE4SS_v4.0.0.zip";
    public const string RuntimeHash = "251ad0d1381a241750e036f5918cbf2b5d7ec58ebc608cb2dd09d459d0c81e45";
    public const string PatcherHash = "9c51138983c066f0dbf0a3de2edf80363c8db05ecc615d8116030e92eafddc31";
    public const string PatchedRuntimeHash = "410e544971bd74e4341f56835398cbd55f6c3569f920d91c72648e859bca67c0";
    public static bool Newer(string available, string installed) => Version.TryParse(available.TrimStart('v'), out var next) && Version.TryParse(installed.TrimStart('v'), out var previous) && next > previous;
    public static bool ValidId(string id) => Regex.IsMatch(id, @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,79}\z") && Maps.ValidId(id);
    public static string ReleaseUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" || uri.Query != "" || uri.Fragment != "" || uri.UserInfo != "" || !uri.IsDefaultPort)
            throw new InvalidDataException("Invalid mods release URL.");
        string[] parts = uri.AbsolutePath.Split('/');
        if (parts.Length != 7 || parts[1] != "Splitgate-Architects" || parts[2] != "Splitgate-Mods" || parts[3] != "releases" || parts[4] != "download" || !Regex.IsMatch(parts[5], @"\Av[0-9]+\.[0-9]+\.[0-9]+\z") || !Regex.IsMatch(parts[6], @"\ASplitgate-Mods(?:-v[0-9.]+)?\.zip\z"))
            throw new InvalidDataException("Mods download is outside the supported release repository.");
        return url;
    }
    public static (string Version, string Url, string Hash) ReleaseMetadata(string json)
    {
        if (json.Length > 2_000_000) throw new InvalidDataException("Mods release metadata is too large.");
        var node = Json.Read(json);
        string version = (string?)node["tag_name"] ?? "";
        if (!Regex.IsMatch(version, @"\Av[0-9]+\.[0-9]+\.[0-9]+\z") || (bool?)node["draft"] == true || (bool?)node["prerelease"] == true)
            throw new InvalidDataException("No supported stable mods release.");
        var assets = node["assets"]!.AsArray().Where(a => ((string?)a?["name"] ?? "").StartsWith("Splitgate-Mods", StringComparison.Ordinal) && ((string?)a?["name"] ?? "").EndsWith(".zip", StringComparison.Ordinal)).ToArray();
        if (assets.Length != 1) throw new InvalidDataException("Expected one mods release ZIP.");
        string url = ReleaseUrl((string)assets[0]!["browser_download_url"]!);
        if (!new Uri(url).AbsolutePath.Contains("/" + version + "/", StringComparison.Ordinal)) throw new InvalidDataException("Mods asset version mismatch.");
        string digest = (string?)assets[0]!["digest"] ?? "";
        if (!Regex.IsMatch(digest, @"\Asha256:[a-fA-F0-9]{64}\z")) throw new InvalidDataException("Mods release has no SHA-256 digest.");
        return (version, url, digest[7..]);
    }
    public static Dictionary<string, byte[]> ReadZip(byte[] bytes)
    {
        if (bytes.Length > 32_000_000) throw new InvalidDataException("Mods ZIP exceeds 32 MB.");
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Count > 5000) throw new InvalidDataException("Too many files in mods ZIP.");
        long total = 0;
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            string path = entry.FullName.TrimEnd('/');
            if (path.Length == 0) continue;
            if (path.Length > 220 || path.Split('/').Any(p => !Maps.ValidId(p)) || entry.FullName.Contains('\\') || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Unsafe path in mods ZIP.");
            if (entry.FullName.EndsWith('/')) continue;
            total += entry.Length;
            if (entry.Length > 32_000_000 || total > 128_000_000) throw new InvalidDataException("Expanded mods ZIP is too large.");
            using var input = entry.Open();
            using var output = new MemoryStream();
            byte[] buffer = new byte[65536];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                if (output.Length + read > entry.Length) throw new InvalidDataException("Invalid ZIP length.");
                output.Write(buffer, 0, read);
            }
            if (output.Length != entry.Length || !files.TryAdd(path, output.ToArray())) throw new InvalidDataException("Duplicate or incomplete mods ZIP entry.");
        }
        return files;
    }
    public static CommunityMod[] Discover(IReadOnlyDictionary<string, byte[]> files)
    {
        const string prefix = "ue4ss/Mods/";
        return files.Keys.Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(p => p[prefix.Length..].Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(id => ValidId(id) && files.Keys.Any(p => p.Equals(prefix + id + "/Scripts/main.lua", StringComparison.OrdinalIgnoreCase)))
            .Select(id => id switch
            {
                "BotChatter" => new CommunityMod(id, "Bot Chatter", "Bot chat, custom names, phrase pools and timing.", "Host only · unmodified clients can see replicated chat and names"),
                "MapEditorBudgetMod" => new CommunityMod(id, "Map Editor Budget", "Configure the Map Lab object budget.", "Map editor · local installation"),
                _ => new CommunityMod(id, Regex.Replace(id, "([a-z])([A-Z])", "$1 $2"), "New community mod. See its documentation for requirements.", "Host/client requirements not declared")
            }).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static Dictionary<string, byte[]> ModFiles(ModBundle bundle, string id)
    {
        if (!ValidId(id) || !bundle.Release.Mods.Any(m => m.Id == id)) throw new InvalidDataException("Mod is not in this release.");
        string prefix = "ue4ss/Mods/" + id + "/";
        var result = bundle.Files.Where(p => p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        // Discovery supports Lua packages. Native DLL mods need a declared compatibility contract.
        if (result.Keys.Any(p => Path.GetExtension(p).ToLowerInvariant() is ".dll" or ".exe" or ".bat" or ".cmd" or ".ps1")) throw new InvalidDataException("This mod includes a native binary or installer that this SGL version does not support.");
        if (!result.Keys.Any(p => p.Equals(prefix + "Scripts/main.lua", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Mod has no Lua entry point.");
        return result;
    }
    public static bool ConfigurationFile(string path) => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/config.lua", StringComparison.OrdinalIgnoreCase);
    public static async Task<byte[]> Fetch(string url, int limit, CancellationToken cancel = default, HttpMessageHandler? handler = null)
    {
        using var client = handler == null ? new HttpClient() : new HttpClient(handler, false);
        client.Timeout = TimeSpan.FromMinutes(2);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SGL-Mods/1.0");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Mods download exceeds its limit.");
        using var input = await response.Content.ReadAsStreamAsync(cancel);
        using var output = new MemoryStream();
        byte[] buffer = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(buffer, cancel)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("Mods download exceeds its limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    public static async Task<ModBundle> Catalog(string cache, bool refresh, CancellationToken cancel = default, HttpMessageHandler? handler = null)
    {
        Directory.CreateDirectory(cache);
        string metadataPath = Path.Combine(cache, "release.json");
        string metadata = !refresh && File.Exists(metadataPath) ? await File.ReadAllTextAsync(metadataPath, cancel)
            : Encoding.UTF8.GetString(await Fetch(Latest, 2_000_000, cancel, handler));
        var release = ReleaseMetadata(metadata);
        string package = Path.Combine(cache, release.Hash.ToUpperInvariant() + ".zip");
        byte[] bytes = File.Exists(package) ? await File.ReadAllBytesAsync(package, cancel) : await Fetch(release.Url, 32_000_000, cancel, handler);
        if (!Json.Hash(bytes).Equals(release.Hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Mods ZIP checksum mismatch.");
        var files = ReadZip(bytes);
        var mods = Discover(files);
        if (mods.Length == 0) throw new InvalidDataException("Release has no supported Lua mods.");
        await File.WriteAllBytesAsync(package, bytes, cancel);
        Json.Atomic(metadataPath, metadata);
        return new(new(release.Version, release.Url, release.Hash, mods), files);
    }
}
