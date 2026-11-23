using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SGL.Core;

static class SharedLibraryTests
{
    public static async Task Run(string work, Action<bool, string> check, Action<Action, string> reject)
    {
        byte[] imageBytes = [0xff, 0xd8, 0xff, 1, 2, 3, 4];
        JsonObject ImageDescriptor(byte[] bytes, string url = "https://sgl-content.s3.us-west-2.amazonaws.com/preview") => new()
        { ["url"] = url, ["size"] = bytes.Length, ["sha256"] = Json.Hash(bytes) };
        using var previewHttp = new PreviewHttp(imageBytes, check);
        check((await LibraryClient.FetchPreview(ImageDescriptor(imageBytes), handler: previewHttp))!.SequenceEqual(imageBytes), "Shared preview bytes changed");
        check(await LibraryClient.FetchPreview(null, handler: previewHttp) == null, "Missing preview did not return an empty result");
        async Task PreviewRejected(JsonObject descriptor, string message, HttpMessageHandler? handler = null)
        {
            bool failed = false;
            try { await LibraryClient.FetchPreview(descriptor, handler: handler ?? previewHttp); }
            catch (Exception error) when (error is InvalidDataException or HttpRequestException) { failed = true; }
            check(failed, message);
        }
        var badHash = ImageDescriptor(imageBytes); badHash["sha256"] = new string('0', 64);
        await PreviewRejected(badHash, "Preview checksum mismatch was accepted");
        var oversized = ImageDescriptor(imageBytes); oversized["size"] = 2_000_001;
        await PreviewRejected(oversized, "Oversized preview was accepted");
        await PreviewRejected(ImageDescriptor(imageBytes, "https://evil.test/image.jpg"), "Preview request escaped S3");
        await PreviewRejected(ImageDescriptor(imageBytes), "Redirected preview was accepted", new PreviewHttp(imageBytes, check, HttpStatusCode.Redirect));
        byte[] wrongFormat = [1, 2, 3, 4];
        await PreviewRejected(ImageDescriptor(wrongFormat), "Invalid preview format was accepted", new PreviewHttp(wrongFormat, check));
        var undersized = ImageDescriptor(imageBytes); undersized["size"] = 4;
        await PreviewRejected(undersized, "Preview download exceeded its declared size");
        string sessionPath = Path.Combine(work, "Session", "sharing.bin");
        var session = new SGL.Desktop.SharingSession(sessionPath, "https://sharing.example.test/");
        var sessionData = new JsonObject { ["token"] = "test-session-never-plaintext", ["expiresAt"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() };
        session.Save(sessionData);
        check(!Encoding.UTF8.GetString(File.ReadAllBytes(sessionPath)).Contains("test-session-never-plaintext"), "Session token was stored as plaintext");
        check((string?)session.Load()?["token"] == "test-session-never-plaintext", "DPAPI session did not round-trip");
        check(new SGL.Desktop.SharingSession(sessionPath, "https://another-service.example.test").Load() == null, "Session decrypted for a different sharing service");
        sessionData["expiresAt"] = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds();
        session.Save(sessionData);
        check(session.Load() == null, "Expired desktop session was accepted");
        session.Clear();
        check(!File.Exists(sessionPath), "Sign-out retained the local session file");
        string mapId = Guid.NewGuid().ToString("N"), modeId = Guid.NewGuid().ToString("N"), bundleId = Guid.NewGuid().ToString("N");
        string mapRevision = Guid.NewGuid().ToString("N"), modeRevision = Guid.NewGuid().ToString("N"), bundleRevision = Guid.NewGuid().ToString("N");
        byte[] map;
        using (var output = new MemoryStream())
        {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("World.cf1047").Open()))
                    writer.Write("shared test world");
                using (var writer = new StreamWriter(zip.CreateEntry("info.json").Open()))
                    writer.Write("{\"name\":\"Shared arena\",\"author\":\"Architect\"}");
            }
            map = output.ToArray();
        }
        var source = Configuration.New("tdm");
        source["Options"]!["CustomName"] = "Original mode";
        byte[] mode = Encoding.UTF8.GetBytes(source.ToJsonString());
        JsonObject Item(string id, string kind, string revision) => new()
        {
            ["id"] = id,
            ["kind"] = kind,
            ["title"] = "Shared " + kind,
            ["ownerId"] = "creator",
            ["latestRevisionId"] = revision,
            ["owner"] = new JsonObject { ["displayName"] = "Host" }
        };
        JsonObject Descriptor(string id, string kind, string revision, byte[] data) => new()
        {
            ["item"] = Item(id, kind, revision),
            ["revision"] = new JsonObject { ["id"] = revision, ["itemId"] = id, ["number"] = 1, ["content"] = new JsonObject { ["sha256"] = Json.Hash(data), ["size"] = data.Length } },
            ["download"] = new JsonObject { ["url"] = "https://sgl-content.s3.us-west-2.amazonaws.com/" + kind, ["sha256"] = Json.Hash(data), ["size"] = data.Length }
        };
        var mapDescriptor = Descriptor(mapId, "map", mapRevision, map);
        mapDescriptor["item"]!["tags"] = SharedTags.Array(["TDM", "Friday event"]);
        mapDescriptor["item"]!["owner"]!["discordId"] = "123456789";
        mapDescriptor["revision"]!["metadata"] = new JsonObject { ["tags"] = SharedTags.Array(["TDM", "Friday event"]) };
        var modeDescriptor = Descriptor(modeId, "mode", modeRevision, mode);
        var bundleDescriptor = new JsonObject
        {
            ["item"] = Item(bundleId, "bundle", bundleRevision),
            ["revision"] = new JsonObject { ["id"] = bundleRevision, ["itemId"] = bundleId, ["content"] = new JsonObject { ["map"] = new JsonObject { ["itemId"] = mapId, ["revisionId"] = mapRevision }, ["mode"] = new JsonObject { ["itemId"] = modeId, ["revisionId"] = modeRevision } } },
            ["dependencies"] = new JsonArray(mapDescriptor.DeepClone(), modeDescriptor.DeepClone())
        };
        var http = new LibraryHttp(bundleDescriptor, map, mode, check);
        using var client = new LibraryClient("https://sharing.example.test", "private-session", http);
        check(LibraryClient.PkceChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", "PKCE challenge did not match RFC vector");
        var parsed = LibraryClient.ParseLink($"https://sharing.example.test/share/{bundleId}?revision={bundleRevision}", client.Root);
        check(parsed.ItemId == bundleId && parsed.RevisionId == bundleRevision, "HTTPS share link lost pinned revision");
        check(LibraryClient.ParseLink($"sgl://library/install?item={bundleId}&revision={bundleRevision}", client.Root) == parsed, "SGL protocol link differs from HTTPS link");
        reject(() => LibraryClient.ParseLink($"https://evil.test/share/{bundleId}", client.Root), "Foreign share service accepted");
        reject(() => LibraryClient.ParseLink($"sgl://library/install?item={bundleId}&api=https://evil.test", client.Root), "Protocol link could change API origin");
        var package = await client.Download(bundleId, bundleRevision);
        check(package.FromService && package.Entries.Count == 2, "Online bundle not downloaded");
        check(package.Entries.Single(e => e.Kind == "map").Data.SequenceEqual(map), "Map bytes changed in download");
        check(await client.UploadMap(map) == mapId, "Map upload identity changed");
        var portable = SharedPackages.Read(SharedPackages.Pack(package));
        check(!portable.FromService && portable.Entries.Count == 2, "Portable file incorrectly claims authenticated attribution");
        check(portable.BundleId == bundleId && portable.BundleRevisionId == bundleRevision, "Portable bundle lost identity");
        check(portable.Entries.Single(e => e.Kind == "mode").Data.SequenceEqual(mode), "Portable mode payload changed");
        var mapEntry = portable.Entries.Single(e => e.Kind == "map");
        check(SharedTags.Equal(SharedTags.Read(mapEntry.Attribution), ["TDM", "Friday event"]), "Portable map lost revision tags");
        var pinnedAttribution = Configuration.Clone(mapEntry.Attribution);
        pinnedAttribution["item"]!["tags"] = SharedTags.Array(["KOTH"]);
        check(SharedTags.Equal(SharedTags.Read(pinnedAttribution), ["TDM", "Friday event"]), "Latest tags replaced pinned revision tags");
        pinnedAttribution["tags"] = SharedTags.Array(["1v1"]);
        check(SharedTags.Read(pinnedAttribution).Single() == "1v1", "Locally exported tags did not override original attribution");
        check(SharedTags.PublicationHash(map, ["TDM"]) != SharedTags.PublicationHash(map, ["KOTH"]), "Tag-only edits reused the old publication identity");
        check(SharedTags.PublicationHash(map, ["TDM", "Friday event"]) == SharedTags.PublicationHash(map, ["Friday event", "TDM"]), "Tag ordering changed publication identity");
        var tagged = new LibraryItem(mapDescriptor["item"]!.AsObject());
        check(tagged.Matches("Friday", "tdm", "HOST") && tagged.Matches("", "TDM", "123456789"), "Shared tag search or publisher filters failed");
        check(!tagged.Matches("", "TD") && !tagged.Matches("", "KOTH") && !tagged.Matches("", "TDM", "another"), "Shared filters included unrelated tags or publishers");
        var importedTags = new MapTags(Path.Combine(work, "tag-import", "map-tags.json"));
        check(importedTags.ImportShared("map", SharedTags.Read(mapEntry.Attribution), null), "First installation did not import tags");
        check(importedTags.ImportShared("map", ["KOTH"], ["TDM", "Friday event"]) && importedTags.Read()["map"].Single() == "KOTH", "Clean revision update did not replace shared tags");
        importedTags.Set("map", ["KOTH", "My event"]);
        check(!importedTags.ImportShared("map", ["SWAT"], ["KOTH"]) && importedTags.Read()["map"].Contains("My event"), "Revision update discarded personal tags");
        importedTags.Set("map", []);
        check(!importedTags.ImportShared("map", ["SWAT"], ["KOTH"]) && !importedTags.Read().ContainsKey("map"), "Revision update restored personally removed tags");
        using (var filtered = new LibraryClient("https://sharing.example.test", handler: new FilterHttp(mapDescriptor["item"]!.AsObject(), check)))
        {
            var page = await filtered.List(false, tag: "Friday event", creator: "Host & friend");
            check(page.Cursor == "next/page" && page.Items.Single().Tags.Contains("TDM"), "Filtered result lost tags or cursor");
            await filtered.List(false, cursor: page.Cursor, tag: "Friday event", creator: "Host & friend");
        }
        byte[] RewritePackage(Action<JsonObject> change)
        {
            using var bytes = new MemoryStream();
            bytes.Write(SharedPackages.Pack(package));
            bytes.Position = 0;
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Update, true))
            {
                var entry = zip.GetEntry("manifest.json")!;
                JsonObject manifest;
                using (var reader = new StreamReader(entry.Open()))
                    manifest = Json.Read(reader.ReadToEnd()).AsObject();
                entry.Delete();
                change(manifest);
                using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
                writer.Write(manifest.ToJsonString());
            }
            return bytes.ToArray();
        }
        reject(() => SharedPackages.Read(RewritePackage(m => m["entries"]![0]!["sha256"] = new string('0', 64))), "Portable checksum tampering was accepted");
        reject(() => SharedPackages.Read(RewritePackage(m => m["entries"]![0]!["path"] = "../outside.bin")), "Portable archive traversal was accepted");
        reject(() => SharedPackages.Read(RewritePackage(m => m["entries"]![0]!["attribution"]!["tags"] = new JsonArray(123))), "Portable package accepted malformed tags");
        using (var retried = new LibraryClient("https://sharing.example.test", handler: new RetryHttp(check)))
            check((bool)(await retried.Send("items", new JsonObject { ["requestId"] = Guid.NewGuid().ToString("N") }))["ok"]!, "Publishing did not recover a transient response");
        bool originRejected = false;
        try
        {
            await client.Send("https://evil.test/items");
        }
        catch (InvalidDataException) { originRejected = true; }
        check(originRejected, "API bearer request escaped the configured origin");
        var corrupt = Configuration.Clone(bundleDescriptor);
        corrupt["dependencies"]![0]!["download"]!["sha256"] = new string('0', 64);
        using (var bad = new LibraryClient("https://sharing.example.test", handler: new LibraryHttp(corrupt, map, mode, (_, _) => { })))
        {
            bool failed = false;
            try
            {
                await bad.Download(bundleId, bundleRevision);
            }
            catch (InvalidDataException) { failed = true; }
            check(failed, "Corrupt shared file was accepted");
        }
        var wrongPin = Configuration.Clone(bundleDescriptor);
        wrongPin["revision"]!["content"]!["mode"]!["revisionId"] = Guid.NewGuid().ToString("N");
        using (var bad = new LibraryClient("https://sharing.example.test", handler: new LibraryHttp(wrongPin, map, mode, (_, _) => { })))
        {
            bool failed = false;
            try
            {
                await bad.Download(bundleId, bundleRevision);
            }
            catch (InvalidDataException) { failed = true; }
            check(failed, "Bundle installed an unpinned dependency");
        }
        string root = Path.Combine(work, "SharedInstall");
        string originalId = Guid.NewGuid().ToString(), originalSave = Guid.NewGuid().ToString();
        string relative = $"CustomModes/{originalId}/{originalSave}.bin", archive = GameStore.Inside(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        File.WriteAllBytes(archive, Modes.Pack(Configuration.Wrapped(source)));
        string index = Path.Combine(root, "CloudSaveManifest.json");
        File.WriteAllText(index, new JsonObject { ["Preserve"] = "metadata", ["Files"] = new JsonArray(new JsonObject { ["FileId"] = originalId, ["FileName"] = "Original mode", ["ContentType"] = "CustomModes", ["OwnerId"] = "recipient", ["Saves"] = new JsonArray(new JsonObject { ["SaveId"] = originalSave, ["RelativePath"] = relative, ["CreatedAt"] = "2026-01-01" }) }) }.ToJsonString(), Encoding.Unicode);
        var store = new GameStore(root, () => false);
        var state = new LibraryState(Path.Combine(work, "SharedProfile", "library.json"));
        var before = store.Read();
        reject(() => new GameStore(root, () => true).InstallShared(before, package, state, "service"), "Shared install ignored running game");
        int guardCalls = 0;
        reject(() => new GameStore(root, () => ++guardCalls > 1).InstallShared(before, package, state, "service"), "Shared transaction ignored second game guard");
        check(store.Read().ManifestHash == before.ManifestHash && store.Read().Maps.Count == 0, "Aborted shared transaction changed registrations");
        var installed = store.InstallShared(before, package, state, "service");
        state.RecordInstall("service", installed.Records);
        var after = store.Read();
        check(after.Modes.Count == 2 && after.Maps.Count == 1, "Bundle installation was not complete");
        check(after.Modes.Single(m => m.Id == originalId).ArchiveHash == before.Modes.Single().ArchiveHash, "Name collision overwrote recipient mode");
        check(after.Modes.Any(m => m.Name.Contains("Shared 1")), "Name collision was not resolved");
        check(after.Manifest["Files"]!.AsArray().OfType<JsonObject>().All(f => (string?)f["OwnerId"] == "recipient"), "Shared install used another machine's owner");
        check((string?)after.Manifest["Preserve"] == "metadata" && File.ReadAllBytes(index)[0] == 255, "Shared install lost encoding or metadata");
        var modeRecord = state.Installed("service", modeId)!;
        string localModeId = (string)modeRecord["localFileId"]!;
        var same = store.InstallShared(after, package, state, "service");
        check(store.Read().Modes.Count == 2 && same.Records.Count == 2, "Repeated shared install duplicated content");
        var modified = Configuration.Clone(after.Modes.Single(m => m.Id == localModeId).Payload);
        Configuration.Set(modified, Configuration.Fields.Single(f => (string)f["id"]! == "precisionDamage"), "99");
        store.Apply(store.Read(), localModeId, modified);
        var locallyEdited = store.Read();
        check(state.HasLocalChanges(locallyEdited, "service", package), "Local shared-item edits were not detected");
        var updatedSource = Configuration.Clone(source);
        Configuration.Set(updatedSource, Configuration.Fields.Single(f => (string)f["id"]! == "precisionDamage"), "10");
        var nextPackage = package with
        {
            Entries = package.Entries.Select(e => e.Kind == "mode" ? e with { RevisionId = Guid.NewGuid().ToString("N"), Data = Encoding.UTF8.GetBytes(updatedSource.ToJsonString()) } : e).ToArray()
        };
        reject(() => store.InstallShared(locallyEdited, nextPackage, state, "service"), "Shared update overwrote local edits");
        var copied = store.InstallShared(locallyEdited, nextPackage, state, "service", copyLocalEdits: true);
        state.RecordInstall("service", copied.Records);
        check(store.Read().Modes.Count == 3, "Local edit was not retained as a separate mode");
        check((double)Configuration.Value(store.Read().Modes.Single(m => m.Id == localModeId).Payload, Configuration.Fields.Single(f => (string)f["id"]! == "precisionDamage"))! == 99, "Local precision edit was lost");
        string newLocalModeId = (string)state.Installed("service", modeId)!["localFileId"]!;
        var nextAgain = nextPackage with
        {
            Entries = nextPackage.Entries.Select(e => e.Kind == "mode" ? e with { RevisionId = Guid.NewGuid().ToString("N") } : e).ToArray()
        };
        var update = store.InstallShared(store.Read(), nextAgain, state, "service");
        state.RecordInstall("service", update.Records);
        check((string?)state.Installed("service", modeId)?["localFileId"] == newLocalModeId && store.Read().Modes.Count == 3, "Unedited shared update lost stable identity");
        check(File.Exists(Path.Combine(installed.BackupDirectory, "CloudSaveManifest.json")), "Shared install has no backup");
        check(File.Exists(Path.Combine(installed.BackupDirectory, "shared-install.json")), "Shared install lacks profile recovery receipt");
        state.RecordPublication("service-user", "mode", originalId, modeId, modeRevision, Json.Hash(mode));
        check((string?)new LibraryState(Path.Combine(work, "SharedProfile", "library.json")).Published("service-user", "mode", originalId)?["itemId"] == modeId, "Publication identity did not persist");
        check(state.Published("another-user", "mode", originalId) == null, "Publication ownership crossed user scope");
    }
    sealed class PreviewHttp(byte[] bytes, Action<bool, string> check, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            check(request.Headers.Authorization == null, "SGL credentials leaked to a preview download");
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) };
            if (status == HttpStatusCode.Redirect) response.Headers.Location = new Uri("https://evil.test/redirect");
            return Task.FromResult(response);
        }
    }
    sealed class FilterHttp(JsonObject item, Action<bool, string> check) : HttpMessageHandler
    {
        int requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var query = request.RequestUri!.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            check(query["tag"] == "Friday event" && query["creator"] == "Host & friend", "API filters were lost or incorrectly URL encoded");
            if (requests++ > 0) check(query["cursor"] == "next/page", "Pagination dropped its cursor or filters");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["items"] = new JsonArray(item.DeepClone()), ["cursor"] = "next/page" }.ToJsonString()) });
        }
    }
    sealed class RetryHttp(Action<bool, string> check) : HttpMessageHandler
    {
        int attempts;
        string? original;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            string current = await request.Content!.ReadAsStringAsync(cancel);
            if (attempts++ == 0)
            {
                original = current;
                return new(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent("upstream unavailable")
                };
            }
            check(original == current, "Retry changed the idempotency identity or publication body");
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}")
            };
        }
    }
    sealed class LibraryHttp(JsonObject bundle, byte[] map, byte[] mode, Action<bool, string> check) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            if (request.RequestUri!.Host.EndsWith("amazonaws.com"))
            {
                check(request.Headers.Authorization == null, "SGL session leaked to S3");
                if (request.Method == HttpMethod.Put)
                {
                    check(request.Headers.IfNoneMatch.Single().Tag == "*", "Upload could overwrite staging object");
                    check((await request.Content!.ReadAsByteArrayAsync(cancel)).SequenceEqual(map), "Uploaded map bytes changed");
                    return new(HttpStatusCode.OK);
                }
                return new(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(request.RequestUri.AbsolutePath.EndsWith("map") ? map : mode)
                };
            }
            if (request.RequestUri.AbsolutePath == "/uploads")
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(new JsonObject { ["uploadId"] = (string)bundle["dependencies"]![0]!["item"]!["id"]!, ["url"] = "https://sgl-content.s3.us-west-2.amazonaws.com/staging/map", ["headers"] = new JsonObject { ["Content-Type"] = "application/octet-stream", ["If-None-Match"] = "*", ["x-amz-checksum-sha256"] = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(map)) } }.ToJsonString())
                };
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(bundle.ToJsonString())
            };
        }
    }
}
