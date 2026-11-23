using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SGL.Core;

static class SharedLibraryTests
{
    public static async Task Run(string work, Action<bool, string> check, Action<Action, string> reject)
    {
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
