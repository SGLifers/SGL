using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace SGL.Core;

public sealed record LibraryItem(JsonObject Json)
{
    public string Id => (string)Json["id"]!;
    public string Title => (string)Json["title"]!;
    public string Kind => (string)Json["kind"]!;
    public string LatestRevisionId => (string)Json["latestRevisionId"]!;
    public string OwnerId => (string)Json["ownerId"]!;
    public string Description => (string?)Json["description"] ?? "";
    public string OwnerName => (string?)Json["owner"]?["displayName"] ?? "Unknown";
    public string Label => $"{Title} · {Kind} · {OwnerName}";
    public override string ToString() => Label;
}
public sealed record LibraryRevision(JsonObject Json)
{
    public string Id => (string)Json["id"]!;
    public string Label => $"Revision {Json["number"]} · {Json["actor"]?["displayName"]} · {Json["changeNote"]}";
    public override string ToString() => Label;
}
public sealed record SharedEntry(string ItemId, string RevisionId, string Kind, string Title, byte[] Data, JsonObject Attribution);
public sealed record SharedPackage(string Title, IReadOnlyList<SharedEntry> Entries, string? BundleId = null, string? BundleRevisionId = null, bool FromService = false);
public sealed class LibraryException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

public sealed class LibraryClient : IDisposable
{
    readonly HttpClient client;
    public Uri Root
    {
        get;
    }
    public string? SessionToken
    {
        get; set;
    }
    public LibraryClient(string root, string? token = null, HttpMessageHandler? handler = null)
    {
        Root = new Uri(root.TrimEnd('/') + "/", UriKind.Absolute);
        if (Root.Scheme != "https" || Root.UserInfo.Length > 0 || Root.Query.Length > 0 || Root.Fragment.Length > 0)
            throw new InvalidDataException("The sharing service must use HTTPS.");
        SessionToken = token;
        client = handler == null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler, false);
        client.Timeout = TimeSpan.FromMinutes(3);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SGL/0.3.0");
    }
    public static bool ValidId(string? id) => id != null && Regex.IsMatch(id, @"\A[a-f0-9]{32}\z");
    public static string PkceVerifier() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string PkceChallenge(string verifier) => Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static async Task<byte[]> Bounded(HttpContent content, long limit, CancellationToken cancel)
    {
        if (content.Headers.ContentLength > limit)
            throw new InvalidDataException("Shared content exceeds its size limit.");
        using var input = await content.ReadAsStreamAsync(cancel);
        using var output = new MemoryStream();
        byte[] buffer = new byte[65_536];
        int read;
        while ((read = await input.ReadAsync(buffer, cancel)) > 0)
        {
            if (output.Length + read > limit)
                throw new InvalidDataException("Shared content exceeds its size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    public async Task<JsonObject> Send(string path, JsonObject? body = null, CancellationToken cancel = default)
    {
        var target = new Uri(Root, path.TrimStart('/'));
        if (!Root.IsBaseOf(target))
            throw new InvalidDataException("API requests must stay within the configured sharing service.");
        string? json = body?.ToJsonString();
        bool retryable = body == null || ValidId((string?)body["requestId"]) && (path == "items" || Regex.IsMatch(path, @"\Aitems/[a-f0-9]{32}/revisions\z"));
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, target);
                if (SessionToken != null)
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SessionToken);
                if (json != null)
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
                if (retryable && attempt < 2 && response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                {
                    await Task.Delay(500 * (attempt + 1), cancel);
                    continue;
                }
                byte[] bytes = await Bounded(response.Content, 1_500_000, cancel);
                JsonObject result;
                try
                {
                    result = Json.Read(Encoding.UTF8.GetString(bytes)).AsObject();
                }
                catch { throw new LibraryException(response.StatusCode, "The sharing service returned an invalid response."); }
                if (!response.IsSuccessStatusCode)
                    throw new LibraryException(response.StatusCode, (string?)result["error"] ?? "The sharing service could not complete this request.");
                return result;
            }
            catch (HttpRequestException) when (retryable && attempt < 2) { await Task.Delay(500 * (attempt + 1), cancel); }
        }
    }
    public async Task<(LibraryItem[] Items, string? Cursor)> List(bool mine, string? kind = null, string? cursor = null, CancellationToken cancel = default)
    {
        var query = new List<string>();
        if (kind != null)
            query.Add("kind=" + Uri.EscapeDataString(kind));
        if (cursor != null)
            query.Add("cursor=" + Uri.EscapeDataString(cursor));
        var data = await Send((mine ? "me/items" : "items") + "?" + string.Join('&', query), cancel: cancel);
        return (data["items"]!.AsArray().Select(n => new LibraryItem(n!.AsObject())).ToArray(), (string?)data["cursor"]);
    }
    public async Task<LibraryItem> Item(string id, CancellationToken cancel = default)
    {
        if (!ValidId(id))
            throw new InvalidDataException("Invalid shared item identity.");
        return new(await Send("items/" + id, cancel: cancel));
    }
    public async Task<(LibraryRevision[] Revisions, string? Cursor)> Revisions(string id, string? cursor = null, CancellationToken cancel = default)
    {
        if (!ValidId(id))
            throw new InvalidDataException("Invalid shared item identity.");
        var result = await Send("items/" + id + "/revisions" + (cursor == null ? "" : "?cursor=" + Uri.EscapeDataString(cursor)), cancel: cancel);
        return (result["revisions"]!.AsArray().Select(n => new LibraryRevision(n!.AsObject())).ToArray(), (string?)result["cursor"]);
    }
    public static (string ItemId, string? RevisionId) ParseLink(string value, Uri service)
    {
        value = value.Trim();
        if (ValidId(value))
            return (value, null);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new InvalidDataException("Paste an SGL share link or item identity.");
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length == 2 ? Uri.UnescapeDataString(p[1]) : "");
        string? id;
        if (uri.Scheme == "sgl" && uri.Host == "library" && uri.AbsolutePath == "/install")
        {
            if (query.Keys.Any(k => k is not ("item" or "revision")))
                throw new InvalidDataException("Unsupported SGL link parameters.");
            id = query.GetValueOrDefault("item");
        }
        else if (uri.Scheme == "https" && uri.GetLeftPart(UriPartial.Authority) == service.GetLeftPart(UriPartial.Authority) && uri.AbsolutePath.StartsWith(service.AbsolutePath.TrimEnd('/') + "/share/", StringComparison.Ordinal))
            id = uri.AbsolutePath.Split('/').Last();
        else
            throw new InvalidDataException("This link does not belong to the configured SGL sharing service.");
        string? revision = query.GetValueOrDefault("revision");
        if (!ValidId(id) || revision != null && !ValidId(revision))
            throw new InvalidDataException("Invalid shared item or revision identity.");
        return (id!, revision);
    }
    static Uri StorageUrl(string value)
    {
        var uri = new Uri(value, UriKind.Absolute);
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || !Regex.IsMatch(uri.Host, @"\A[a-z0-9.-]+\.s3(?:[.-][a-z0-9-]+)?\.amazonaws\.com\z"))
            throw new InvalidDataException("Shared file URL is outside AWS S3.");
        return uri;
    }
    public async Task<SharedPackage> Download(string id, string revision, CancellationToken cancel = default)
    {
        if (!ValidId(id) || !ValidId(revision))
            throw new InvalidDataException("Invalid shared item or revision identity.");
        var descriptor = await Send($"items/{id}/revisions/{revision}", cancel: cancel);
        var item = descriptor["item"]!.AsObject();
        var entries = new List<SharedEntry>();
        async Task Read(JsonObject data)
        {
            var entryItem = data["item"]!.AsObject();
            var entryRevision = data["revision"]!.AsObject();
            string kind = (string)entryItem["kind"]!, itemId = (string)entryItem["id"]!, revisionId = (string)entryRevision["id"]!;
            if (kind is not ("map" or "mode") || !ValidId(itemId) || !ValidId(revisionId) || (string?)entryRevision["itemId"] != itemId)
                throw new InvalidDataException("Invalid shared content descriptor.");
            var download = data["download"]!.AsObject();
            long size = (long)download["size"]!;
            long maximum = kind == "map" ? Maps.MaximumArchive : 1_048_576;
            if (size < 1 || size > maximum)
                throw new InvalidDataException("Shared file exceeds its size limit.");
            using var response = await client.GetAsync(StorageUrl((string)download["url"]!), HttpCompletionOption.ResponseHeadersRead, cancel);
            if (!response.IsSuccessStatusCode)
                throw new IOException("The shared file could not be downloaded. Reload the revision and retry.");
            var bytes = await Bounded(response.Content, size, cancel);
            if (bytes.LongLength != size || !Json.Hash(bytes).Equals((string?)download["sha256"], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Shared file checksum or size did not match.");
            if (kind == "map")
                Maps.Inspect(bytes);
            else
                Modes.Validate(Configuration.Wrapped(Json.Read(Encoding.UTF8.GetString(bytes)).AsObject()));
            entries.Add(new(itemId, revisionId, kind, (string)entryItem["title"]!, bytes, new JsonObject { ["item"] = entryItem.DeepClone(), ["revision"] = entryRevision.DeepClone() }));
        }
        if ((string?)item["kind"] == "bundle")
        {
            var dependencies = descriptor["dependencies"]!.AsArray();
            if (dependencies.Count != 2)
                throw new InvalidDataException("A shared bundle must contain one map and one mode.");
            foreach (var dependency in dependencies)
                await Read(dependency!.AsObject());
            if (entries.Count(e => e.Kind == "map") != 1 || entries.Count(e => e.Kind == "mode") != 1)
                throw new InvalidDataException("Invalid bundle contents.");
            foreach (var entry in entries)
                if ((string?)descriptor["revision"]?["content"]?[entry.Kind]?["itemId"] != entry.ItemId || (string?)descriptor["revision"]?["content"]?[entry.Kind]?["revisionId"] != entry.RevisionId)
                    throw new InvalidDataException("Bundle dependencies do not match its pinned revisions.");
        }
        else
            await Read(descriptor);
        if ((string?)item["id"] != id || (string?)descriptor["revision"]?["id"] != revision)
            throw new InvalidDataException("The service returned a different revision.");
        return new((string)item["title"]!, entries, (string?)item["kind"] == "bundle" ? id : null, (string?)item["kind"] == "bundle" ? revision : null, true);
    }
    public async Task<string> UploadMap(byte[] data, CancellationToken cancel = default)
    {
        Maps.Inspect(data);
        var upload = await Send("uploads", new JsonObject { ["sha256"] = Json.Hash(data).ToLowerInvariant(), ["size"] = data.Length }, cancel);
        using var request = new HttpRequestMessage(HttpMethod.Put, StorageUrl((string)upload["url"]!));
        request.Content = new ByteArrayContent(data);
        foreach (var header in upload["headers"]!.AsObject())
        {
            if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse((string)header.Value!);
            else if (header.Key is "x-amz-checksum-sha256" or "If-None-Match")
                request.Headers.TryAddWithoutValidation(header.Key, (string)header.Value!);
            else
                throw new InvalidDataException("Unsupported upload header.");
        }
        using var response = await client.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode)
            throw new IOException("Map upload did not complete. Try publishing again.");
        return (string)upload["uploadId"]!;
    }
    public void Dispose() => client.Dispose();
}
