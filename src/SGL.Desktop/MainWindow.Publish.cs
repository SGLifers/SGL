using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using SGL.Core;
namespace SGL.Desktop;

public partial class MainWindow
{
    sealed record PublishSource(string Id, string Name, string Kind, byte[] Data, JsonObject Attribution);
    PublishSource LocalModeSource()
    {
        if (PublishMode.SelectedItem is not LocalShareSource selected)
            throw new InvalidOperationException("Choose a local gamemode.");
        JsonObject value;
        string id;
        if (selected.Id == "__editor__")
        {
            EnsureValid();
            value = Configuration.Clone(payload!);
            id = selectedId ?? editorShareId;
        }
        else
        {
            var saved = snapshot?.Modes.SingleOrDefault(m => m.Id == selected.Id) ?? throw new IOException("Refresh the saved gamemodes first.");
            value = Configuration.Clone(saved.Payload);
            id = saved.Id;
        }
        Modes.Validate(Configuration.Wrapped(value));
        return new(id, Configuration.Name(value), "mode", Encoding.UTF8.GetBytes(value.ToJsonString()), SourceAttribution("mode", id));
    }
    PublishSource LocalMapSource()
    {
        var map = PublishMap.SelectedItem as SavedMap ?? throw new InvalidOperationException("Choose a local map.");
        if (store == null || snapshot == null || !snapshot.Maps.Any(m => m.Id == map.Id))
            throw new IOException("Refresh the save folder first.");
        if (new FileInfo(map.ArchivePath).Length > Maps.MaximumArchive)
            throw new InvalidDataException("Map exceeds 128 MB.");
        byte[] bytes = File.ReadAllBytes(map.ArchivePath);
        if (Json.Hash(bytes) != map.ArchiveHash)
            throw new IOException("This map changed after Refresh. Refresh before publishing.");
        Maps.Inspect(bytes);
        var attribution = SourceAttribution("map", map.Id);
        if (attribution.Count == 0)
            attribution["credits"] = map.Author;
        return new(map.Id, map.Name, "map", bytes, attribution);
    }
    JsonObject SourceAttribution(string kind, string localId)
    {
        var origin = SharedState.OriginForLocal(LibraryRoot, kind, localId) ?? SharedState.OriginForLocal("offline", kind, localId);
        return origin?["attribution"] is JsonObject attribution ? Configuration.Clone(attribution) : new();
    }
    static string PortableId(string kind, string id) => Guid.TryParse(id, out var parsed) ? parsed.ToString("N") : Json.Hash(Encoding.UTF8.GetBytes(kind + ":" + id))[..32].ToLowerInvariant();
    void LibraryExportLocal(object sender, RoutedEventArgs e) => Run(() =>
    {
        var sources = ShareKind == "bundle" ? new[] { LocalMapSource(), LocalModeSource() } : new[] { ShareKind == "map" ? LocalMapSource() : LocalModeSource() };
        var entries = sources.Select(s => new SharedEntry(PortableId(s.Kind, s.Id), Json.Hash(s.Data)[..32].ToLowerInvariant(), s.Kind, s.Name, s.Data, s.Attribution)).ToArray();
        SaveSharedPackage(new(PublishTitle.Text.Trim().Length == 0 ? sources[0].Name : PublishTitle.Text.Trim(), entries));
    });
    async void LibraryPublish(object sender, RoutedEventArgs e) => await LibraryWork(async cancel =>
    {
        using var client = SharingClient();
        var user = Session(client.Root.AbsoluteUri).Load()?["user"]?.AsObject() ?? throw new InvalidOperationException("Sign in with Discord before publishing.");
        string kind = ShareKind, visibility = (string)((ComboBoxItem)PublishVisibility.SelectedItem).Tag, action = (string)((ComboBoxItem)PublishAction.SelectedItem).Tag;
        string title = PublishTitle.Text.Trim(), description = PublishDescription.Text.Trim(), credits = PublishCredits.Text.Trim(), note = PublishNote.Text.Trim();
        if (title.Length is < 1 or > 120 || note.Length == 0)
            throw new InvalidDataException("Enter a title and change note before publishing.");
        var sources = kind == "bundle" ? new[] { LocalMapSource(), LocalModeSource() } : new[] { kind == "map" ? LocalMapSource() : LocalModeSource() };
        var selected = LibraryItems.SelectedItem as LibraryItem;
        var selectedRevision = LibraryRevisions.SelectedItem as LibraryRevision;
        if (action != "new" && (selected == null || selected.Kind != kind || selectedRevision == null))
            throw new InvalidOperationException("Select a shared item of the same type in Browse first.");
        if (action == "update" && selected!.OwnerId != (string?)user["id"])
            throw new InvalidOperationException("Only the publisher can add revisions. Choose My own version for another creator’s item.");
        string summary = string.Join("\n", sources.Select(s => "• " + s.Name + " (" + s.Kind + ")"));
        if (!Confirm($"Publish “{title}” as {visibility}?\n\n{summary}\n\nPublished by: {user["displayName"]}\nChange note: {note}\n\n{(kind == "bundle" ? "The selected map and mode will be published as needed, then paired at exact revisions." : "The selected content and creator credits will be uploaded.")}"))
            return;
        LibraryStatus.Text = "Publishing selected content…";
        string publicationScope = client.Root.AbsoluteUri + "#" + (string)user["id"]!;
        JsonObject Form(string itemTitle) => new()
        {
            ["requestId"] = Guid.NewGuid().ToString("N"),
            ["title"] = itemTitle,
            ["description"] = description,
            ["credits"] = credits,
            ["changeNote"] = note,
            ["visibility"] = visibility
        };
        async Task<JsonObject> Content(PublishSource source)
        {
            return source.Kind == "mode" ? new JsonObject { ["payload"] = Json.Read(Encoding.UTF8.GetString(source.Data)) } : new JsonObject { ["uploadId"] = await client.UploadMap(source.Data, cancel) };
        }
        async Task<JsonObject> PublishAsset(PublishSource source, bool bundleDependency)
        {
            var previous = SharedState.Published(publicationScope, source.Kind, source.Id);
            JsonObject form = Form(bundleDependency ? source.Name : title);
            form["kind"] = source.Kind;
            string? updateId = null;
            if (bundleDependency && previous != null)
            {
                var publishedItem = await client.Item((string)previous["itemId"]!, cancel);
                string currentVisibility = (string)publishedItem.Json["visibility"]!;
                bool compatible = visibility == "private" || visibility == "unlisted" && currentVisibility != "private" || visibility == "public" && currentVisibility == "public";
                if ((string?)previous["contentHash"] == Json.Hash(source.Data) && compatible)
                    return new JsonObject { ["itemId"] = (string)previous["itemId"]!, ["revisionId"] = (string)previous["revisionId"]! };
                if (compatible)
                {
                    updateId = publishedItem.Id;
                    form["expectedRevisionId"] = (string)previous["revisionId"]!;
                    form["visibility"] = currentVisibility;
                }
                else
                    form["forkOf"] = new JsonObject { ["itemId"] = (string)previous["itemId"]!, ["revisionId"] = (string)previous["revisionId"]! };
            }
            else if (!bundleDependency && action == "update")
            {
                updateId = selected!.Id;
                form["expectedRevisionId"] = selected.LatestRevisionId;
            }
            else
            {
                if (!bundleDependency && action == "fork")
                    form["forkOf"] = new JsonObject { ["itemId"] = selected!.Id, ["revisionId"] = selectedRevision!.Id };
                else if (source.Attribution["item"] is JsonObject parent && source.Attribution["revision"] is JsonObject original && LibraryClient.ValidId((string?)parent["id"]) && LibraryClient.ValidId((string?)original["id"]))
                    form["forkOf"] = new JsonObject { ["itemId"] = (string)parent["id"]!, ["revisionId"] = (string)original["id"]! };
                if (source.Attribution["item"]?["source"] is JsonValue url)
                    form["source"] = url.DeepClone();
            }
            if (credits.Length == 0)
                form["credits"] = (string?)source.Attribution["item"]?["credits"] ?? (string?)source.Attribution["credits"] ?? "";
            form["content"] = await Content(source);
            var result = await client.Send(updateId == null ? "items" : $"items/{updateId}/revisions", form, cancel);
            SharedState.RecordPublication(publicationScope, source.Kind, source.Id, (string)result["itemId"]!, (string)result["revisionId"]!, Json.Hash(source.Data));
            return result;
        }
        JsonObject published;
        if (kind == "bundle")
        {
            var map = await PublishAsset(sources.Single(s => s.Kind == "map"), true);
            var mode = await PublishAsset(sources.Single(s => s.Kind == "mode"), true);
            var form = Form(title);
            form["kind"] = "bundle";
            form["content"] = new JsonObject
            {
                ["map"] = new JsonObject { ["itemId"] = (string)map["itemId"]!, ["revisionId"] = (string)map["revisionId"]! },
                ["mode"] = new JsonObject { ["itemId"] = (string)mode["itemId"]!, ["revisionId"] = (string)mode["revisionId"]! }
            };
            if (action == "update")
                form["expectedRevisionId"] = selected!.LatestRevisionId;
            if (action == "fork")
                form["forkOf"] = new JsonObject { ["itemId"] = selected!.Id, ["revisionId"] = selectedRevision!.Id };
            published = await client.Send(action == "update" ? $"items/{selected!.Id}/revisions" : "items", form, cancel);
        }
        else
            published = await PublishAsset(sources[0], false);
        LibraryLink.Text = (string)published["shareUrl"]!;
        Clipboard.SetText(LibraryLink.Text);
        LibraryStatus.Text = "Published. The link to this exact revision has been copied. Your local game saves were not changed.";
    });
}
