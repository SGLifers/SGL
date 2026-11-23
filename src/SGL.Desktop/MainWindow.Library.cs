using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SGL.Core;
namespace SGL.Desktop;

public partial class MainWindow
{
    bool libraryBusy, updatingShareSources;
    CancellationTokenSource? libraryRequest;
    readonly List<LibraryItem> sharedItems = [];
    string? libraryCursor, revisionsCursor;
    string editorShareId = Guid.NewGuid().ToString("N");
    string LibraryRoot => !string.IsNullOrWhiteSpace((string?)Preferences()["sharingApi"]) ? (string)Preferences()["sharingApi"]! : (string?)Json.Read(Modes.Resource("library-service.json"))["apiUrl"] ?? "";
    LibraryState SharedState => new(Path.Combine(Path.GetDirectoryName(HistoryDirectory)!, "shared-library.json"));
    SharingSession Session(string root) => new(Path.Combine(dataRoot, "Session", "sharing.bin"), root);
    LibraryClient SharingClient()
    {
        string root = LibraryRoot;
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("Sharing is awaiting service deployment. Portable package export and import work now. Set the deployed HTTPS service URL in Settings when available.");
        return new(root, (string?)Session(root).Load()?["token"]);
    }
    void InitializeSharing()
    {
        LibraryServiceUrl.Text = LibraryRoot;
        RefreshSharingAccount();
        Closing += (s, e) => libraryRequest?.Cancel();
    }
    void RefreshSharingAccount()
    {
        var user = string.IsNullOrWhiteSpace(LibraryRoot) ? null : Session(LibraryRoot).Load()?["user"];
        LibraryAccount.Text = user == null ? "Not signed in" : "Signed in as " + (string?)user["displayName"];
        if (string.IsNullOrWhiteSpace(LibraryRoot))
            LibraryStatus.Text = "Sharing service not configured yet. You can export and install portable packages offline.";
    }
    async Task LibraryWork(Func<CancellationToken, Task> action)
    {
        if (libraryBusy)
        {
            LibraryStatus.Text = "An operation is already in progress. Use Cancel to stop it.";
            return;
        }
        libraryBusy = true;
        LibraryItems.IsEnabled = false;
        LibraryMine.IsEnabled = false;
        using var request = new CancellationTokenSource();
        libraryRequest = request;
        try
        {
            await action(request.Token);
        }
        catch (OperationCanceledException) { LibraryStatus.Text = "Operation canceled. Server actions that already completed are retained."; }
        catch (LibraryException error) when (error.Status == HttpStatusCode.Unauthorized)
        {
            if (!string.IsNullOrWhiteSpace(LibraryRoot))
                Session(LibraryRoot).Clear();
            RefreshSharingAccount();
            LibraryStatus.Text = error.Message;
        }
        catch (Exception error) { LibraryStatus.Text = error.Message; Report(error); }
        finally { libraryRequest = null; libraryBusy = false; LibraryItems.IsEnabled = true; LibraryMine.IsEnabled = true; }
    }
    async void LibrarySignIn(object sender, RoutedEventArgs e) => await LibraryWork(async cancel =>
    {
        using var client = SharingClient();
        string verifier = LibraryClient.PkceVerifier();
        var start = await client.Send("auth/start", new JsonObject { ["challenge"] = LibraryClient.PkceChallenge(verifier) }, cancel);
        var uri = new Uri((string)start["authorizationUrl"]!);
        var parameters = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length == 2 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "");
        if (uri.Scheme != "https" || uri.Host != "discord.com" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/oauth2/authorize" || parameters.GetValueOrDefault("scope") != "identify" || parameters.GetValueOrDefault("response_type") != "code")
            throw new InvalidDataException("The service returned an unexpected Discord authorization request.");
        OpenPath(uri.AbsoluteUri);
        LibraryStatus.Text = "Complete Discord sign-in in your browser. Waiting for confirmation…";
        for (int i = 0; i < 300; i++)
        {
            await Task.Delay(2000, cancel);
            var result = await client.Send("auth/poll", new JsonObject { ["requestId"] = (string)start["requestId"]!, ["verifier"] = verifier }, cancel);
            if ((string?)result["status"] != "ready")
                continue;
            Session(client.Root.AbsoluteUri).Save(result);
            RefreshSharingAccount();
            LibraryStatus.Text = "Signed in. Publications and revision attribution use this Discord account.";
            return;
        }
        throw new IOException("Sign-in expired. Try again.");
    });
    async void LibrarySignOut(object sender, RoutedEventArgs e) => await LibraryWork(async cancel =>
    {
        using var client = SharingClient();
        try
        {
            if (client.SessionToken != null)
                await client.Send("auth/logout", new JsonObject(), cancel);
        }
        finally { Session(client.Root.AbsoluteUri).Clear(); RefreshSharingAccount(); }
        LibraryStatus.Text = "Signed out.";
    });
    void LibraryCancel(object sender, RoutedEventArgs e) => libraryRequest?.Cancel();
    async void LibraryRefresh(object sender, RoutedEventArgs e) => await LibraryWork(async cancel =>
    {
        using var client = SharingClient();
        LibraryStatus.Text = "Loading shared items…";
        var page = await client.List(LibraryMine.IsChecked == true, cancel: cancel);
        sharedItems.Clear();
        sharedItems.AddRange(page.Items);
        libraryCursor = page.Cursor;
        FilterSharedItems();
        LibraryStatus.Text = sharedItems.Count + " shared items loaded. Use Load more for additional results.";
    });
    async void LibraryMore(object sender, RoutedEventArgs e) => await LibraryWork(async cancel =>
    {
        if (libraryCursor == null)
        {
            LibraryStatus.Text = "All available items have been loaded.";
            return;
        }
        using var client = SharingClient();
        var page = await client.List(LibraryMine.IsChecked == true, cursor: libraryCursor, cancel: cancel);
        sharedItems.AddRange(page.Items);
        libraryCursor = page.Cursor;
        FilterSharedItems();
    });
    void LibrarySearchChanged(object sender, TextChangedEventArgs e) => FilterSharedItems();
    void FilterSharedItems()
    {
        if (LibraryItems != null)
            LibraryItems.ItemsSource = sharedItems.Where(i => i.Label.Contains(LibrarySearch.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    async void LibraryItemSelected(object sender, SelectionChangedEventArgs e)
    {
        if (LibraryItems.SelectedItem is LibraryItem item && !libraryBusy)
            await LibraryWork(cancel => LoadSharedItem(item, null, cancel));
    }
    async Task LoadSharedItem(LibraryItem item, string? pinned, CancellationToken cancel)
    {
        using var client = SharingClient();
        var revisions = await client.Revisions(item.Id, cancel: cancel);
        var choices = revisions.Revisions.ToList();
        string selected = pinned ?? item.LatestRevisionId;
        if (choices.All(r => r.Id != selected))
        {
            var descriptor = await client.Send($"items/{item.Id}/revisions/{selected}", cancel: cancel);
            choices.Insert(0, new LibraryRevision(descriptor["revision"]!.AsObject()));
        }
        revisionsCursor = revisions.Cursor;
        LibraryDetails.Text = $"{item.Title}\n{item.Kind} · Published by {item.OwnerName}\nDiscord ID: {item.Json["owner"]?["discordId"]}\n{item.Description}\n\nOriginal creator credits: {item.Json["credits"]}\nVisibility: {item.Json["visibility"]}";
        if (item.Json["forkOf"] is JsonObject fork)
            LibraryDetails.Text += "\nBased on " + fork["itemId"] + ", revision " + fork["revisionId"] + ". Original credits: " + fork["credits"];
        LibraryRevisions.ItemsSource = choices;
        LibraryRevisions.SelectedItem = choices.First(r => r.Id == selected);
        LibraryCompareRevision.ItemsSource = choices;
        LibraryCompareRevision.SelectedItem = choices.FirstOrDefault(r => r.Id != selected) ?? choices.First();
        LibraryDiff.Text = "";
    }
    async void LibraryOpenLink(object sender, RoutedEventArgs e) => await OpenLibraryLink(LibraryLink.Text);
    async void LibraryOlderRevisions(object sender, RoutedEventArgs e) => await LibraryWork(async cancel =>
    {
        var selected = SelectedShared();
        if (revisionsCursor == null)
        {
            LibraryStatus.Text = "All revisions have been loaded.";
            return;
        }
        using var client = SharingClient();
        var page = await client.Revisions(selected.Item.Id, revisionsCursor, cancel);
        string? comparison = (LibraryCompareRevision.SelectedItem as LibraryRevision)?.Id;
        var choices = LibraryRevisions.Items.Cast<LibraryRevision>().Concat(page.Revisions).DistinctBy(r => r.Id).ToList();
        revisionsCursor = page.Cursor;
        LibraryRevisions.ItemsSource = choices;
        LibraryRevisions.SelectedItem = choices.First(r => r.Id == selected.Revision.Id);
        LibraryCompareRevision.ItemsSource = choices;
        LibraryCompareRevision.SelectedItem = choices.FirstOrDefault(r => r.Id == comparison) ?? choices.Last();
        LibraryStatus.Text = choices.Count + " revisions loaded.";
    });
    async Task OpenLibraryLink(string link) => await LibraryWork(async cancel =>
    {
        using var client = SharingClient();
        var parsed = LibraryClient.ParseLink(link, client.Root);
        var item = await client.Item(parsed.ItemId, cancel);
        if (sharedItems.All(i => i.Id != item.Id))
            sharedItems.Insert(0, item);
        FilterSharedItems();
        LibraryItems.SelectedItem = sharedItems.First(i => i.Id == item.Id);
        await LoadSharedItem(item, parsed.RevisionId, cancel);
        LibraryStatus.Text = "Shared item opened. Review the revision before installing.";
    });
    public async void AcceptSharedLink(string link)
    {
        SharedTab.IsSelected = true;
        LibraryPages.SelectedIndex = 0;
        LibraryLink.Text = link;
        await OpenLibraryLink(link);
    }
    (LibraryItem Item, LibraryRevision Revision) SelectedShared()
    {
        if (LibraryItems.SelectedItem is not LibraryItem item || LibraryRevisions.SelectedItem is not LibraryRevision revision)
            throw new InvalidOperationException("Select a shared item and revision first.");
        return (item, revision);
    }
    async void LibraryInstall(object sender, RoutedEventArgs e) => await LibraryWork(async cancel =>
    {
        if (store == null || snapshot == null)
            throw new IOException("Refresh the CloudSave folder first.");
        var target = store;
        var expected = snapshot;
        var selection = SelectedShared();
        using var client = SharingClient();
        LibraryStatus.Text = "Downloading and checking the selected revision…";
        var package = await client.Download(selection.Item.Id, selection.Revision.Id, cancel);
        if (store != target || snapshot != expected)
            throw new IOException("The save folder changed during download. Refresh and retry.");
        InstallSharedPackage(package, client.Root.AbsoluteUri);
    });
    void InstallSharedPackage(SharedPackage package, string origin)
    {
        if (store == null || snapshot == null)
            throw new IOException("Refresh the CloudSave folder first.");
        bool copy = SharedState.HasLocalChanges(snapshot, origin, package);
        string content = string.Join("\n", package.Entries.Select(e => $"• {e.Title} ({e.Kind})"));
        string prompt = $"Install “{package.Title}”?\n\n{content}\n\nA complete backup is made first.";
        if (copy)
            prompt += "\nLocal edits were detected. Separate copies will be installed to keep your edits.";
        if (!package.FromService)
            prompt += "\nCreator attribution in this offline file is supplied by the file and is not authenticated.";
        if (!Confirm(prompt))
            return;
        history?.Capture(snapshot, "Checkpoint before shared installation");
        var result = store.InstallShared(snapshot, package, SharedState, origin, copy);
        if (result.BackupDirectory.Length > 0)
            lastBackup = result.BackupDirectory;
        try
        {
            SharedState.RecordInstall(origin, result.Records);
            if (result.Records.Count == 2)
            {
                string map = (string)result.Records.Single(r => (string?)r["kind"] == "map")["localFileId"]!;
                string mode = (string)result.Records.Single(r => (string?)r["kind"] == "mode")["localFileId"]!;
                Links.Set(map, mode, true);
            }
        }
        catch (Exception error) { throw new IOException("Content was installed, but SGL could not save its local tracking. Recovery metadata is in " + result.BackupDirectory, error); }
        history?.Capture(store.Read(), "Installed shared content: " + package.Title);
        Refresh();
        LibraryStatus.Text = result.BackupDirectory.Length == 0 ? "This revision is already installed." : "Installed " + package.Title + ". Choose the map and gamemode separately in the game lobby. Backup: " + result.BackupDirectory;
    }
    async void LibraryCompare(object sender, RoutedEventArgs e) => await LibraryWork(async cancel =>
    {
        var selected = SelectedShared();
        using var client = SharingClient();
        var package = await client.Download(selected.Item.Id, selected.Revision.Id, cancel);
        var sections = new List<string>();
        foreach (var entry in package.Entries)
        {
            var local = SharedState.Installed(client.Root.AbsoluteUri, entry.ItemId);
            string? id = (string?)local?["localFileId"];
            if (entry.Kind == "mode")
            {
                var current = snapshot?.Modes.FirstOrDefault(m => m.Id == id)?.Payload ?? payload;
                sections.Add(entry.Title + "\n" + (current == null ? "No local mode to compare." : Configuration.Diff(current, Json.Read(Encoding.UTF8.GetString(entry.Data)))));
            }
            else
                sections.Add(entry.Title + "\nDownloaded map SHA-256: " + Json.Hash(entry.Data) + "\nMap geometry comparisons are not available.");
        }
        LibraryDiff.Text = string.Join("\n\n", sections);
    });
    void LibraryCopyLink(object sender, RoutedEventArgs e) => Run(() =>
    {
        var selected = SelectedShared();
        using var client = SharingClient();
        Clipboard.SetText(new Uri(client.Root, "share/" + selected.Item.Id + "?revision=" + selected.Revision.Id).AbsoluteUri);
        LibraryStatus.Text = "Share link copied. It points to the selected revision.";
    });
    void SharedRevisionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (LibraryRevisionAttribution != null && LibraryRevisions.SelectedItem is LibraryRevision revision)
            LibraryRevisionAttribution.Text = $"Edited by {revision.Json["actor"]?["displayName"]} · Discord ID {revision.Json["actor"]?["discordId"]}\n{revision.Json["createdAt"]}";
    }
    void LibraryCreatorProfile(object sender, RoutedEventArgs e) => Run(() =>
    {
        var item = SelectedShared().Item;
        string id = (string?)item.Json["owner"]?["discordId"] ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"\A\d{5,24}\z"))
            throw new InvalidDataException("This creator has no Discord profile identity.");
        OpenPath("https://discord.com/users/" + id);
    });
    async void LibraryCompareVersions(object sender, RoutedEventArgs e) => await LibraryWork(async cancel =>
    {
        var selected = SelectedShared();
        if (LibraryCompareRevision.SelectedItem is not LibraryRevision other)
            throw new InvalidOperationException("Select another revision to compare.");
        using var client = SharingClient();
        var a = await client.Send($"items/{selected.Item.Id}/revisions/{other.Id}", cancel: cancel);
        var b = await client.Send($"items/{selected.Item.Id}/revisions/{selected.Revision.Id}", cancel: cancel);
        var lines = new List<string> { other.Label + " → " + selected.Revision.Label };
        static JsonObject? Find(JsonObject descriptor, string kind) => (string?)descriptor["item"]?["kind"] == kind ? descriptor : descriptor["dependencies"]?.AsArray().OfType<JsonObject>().FirstOrDefault(d => (string?)d["item"]?["kind"] == kind);
        var ma = Find(a, "mode");
        var mb = Find(b, "mode");
        if (ma != null && mb != null)
        {
            var first = await client.Download((string)ma["item"]!["id"]!, (string)ma["revision"]!["id"]!, cancel);
            var second = await client.Download((string)mb["item"]!["id"]!, (string)mb["revision"]!["id"]!, cancel);
            lines.Add(Configuration.Diff(Json.Read(Encoding.UTF8.GetString(first.Entries.Single().Data)), Json.Read(Encoding.UTF8.GetString(second.Entries.Single().Data))));
        }
        var mapa = Find(a, "map");
        var mapb = Find(b, "map");
        if (mapa != null && mapb != null)
            lines.Add((string?)mapa["revision"]?["content"]?["sha256"] == (string?)mapb["revision"]?["content"]?["sha256"] ? "Map file unchanged." : "Map file changed. Geometry comparisons are not available.");
        LibraryDiff.Text = string.Join("\n\n", lines);
    });
    async void LibraryExport(object sender, RoutedEventArgs e) => await LibraryWork(async cancel =>
    {
        var selected = SelectedShared();
        using var client = SharingClient();
        var package = await client.Download(selected.Item.Id, selected.Revision.Id, cancel);
        SaveSharedPackage(package);
    });
    void SaveSharedPackage(SharedPackage package)
    {
        var dialog = new SaveFileDialog { Filter = "SGL shared package|*.sglpack", FileName = "SGL-Package.sglpack" };
        if (dialog.ShowDialog(this) == true)
        {
            File.WriteAllBytes(dialog.FileName, SharedPackages.Pack(package));
            LibraryStatus.Text = "Portable package exported with creator credits and exact content versions.";
        }
    }
    void LibraryImportPackage(object sender, RoutedEventArgs e) => Run(() =>
    {
        var dialog = new OpenFileDialog { Filter = "SGL shared package|*.sglpack" };
        if (dialog.ShowDialog(this) != true)
            return;
        if (new FileInfo(dialog.FileName).Length > 130_000_000)
            throw new InvalidDataException("Package exceeds 130 MB.");
        InstallSharedPackage(SharedPackages.Read(File.ReadAllBytes(dialog.FileName)), "offline");
    });
    void SaveLibraryService(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (libraryBusy)
            throw new InvalidOperationException("Finish or cancel the sharing operation first.");
        string value = LibraryServiceUrl.Text.Trim();
        if (value.Length > 0)
        {
            using var client = new LibraryClient(value);
            value = client.Root.AbsoluteUri;
        }
        if (LibraryRoot.Length > 0)
            Session(LibraryRoot).Clear();
        var preferences = Preferences();
        if (value.Length > 0)
            preferences["sharingApi"] = value;
        else
            preferences.Remove("sharingApi");
        Json.Atomic(Path.Combine(dataRoot, "settings.json"), preferences.ToJsonString());
        sharedItems.Clear();
        FilterSharedItems();
        RefreshSharingAccount();
        if (LibraryRoot.Length > 0)
            LibraryStatus.Text = "Sharing service saved. Sign in to publish; local editing is unchanged.";
    });
    void EnableSharedLinks(object sender, RoutedEventArgs e) => Run(() => { SharedLinkActivation.RegisterProtocol(); LibraryStatus.Text = "Open in SGL links are enabled for this Windows account."; });
    sealed record LocalShareSource(string Id, string Name);
    void RefreshSharingSources()
    {
        if (PublishMode == null || PublishMap == null)
            return;
        updatingShareSources = true;
        string? mode = (PublishMode.SelectedItem as LocalShareSource)?.Id, map = (PublishMap.SelectedItem as SavedMap)?.Id;
        var modes = new List<LocalShareSource>();
        if (payload != null)
            modes.Add(new("__editor__", "Current editor: " + Configuration.Name(payload)));
        modes.AddRange(snapshot?.Modes.Select(m => new LocalShareSource(m.Id, m.Name)) ?? []);
        PublishMode.ItemsSource = modes;
        PublishMode.SelectedItem = modes.FirstOrDefault(m => m.Id == mode) ?? modes.FirstOrDefault();
        PublishMap.ItemsSource = snapshot?.Maps ?? [];
        PublishMap.SelectedItem = snapshot?.Maps.FirstOrDefault(m => m.Id == map) ?? snapshot?.Maps.FirstOrDefault();
        updatingShareSources = false;
        PublishMode.IsEnabled = ShareKind is "mode" or "bundle";
        PublishMap.IsEnabled = ShareKind is "map" or "bundle";
        if (PublishTitle.Text.Length == 0)
            PublishSourceChanged(this, new SelectionChangedEventArgs(ComboBox.SelectionChangedEvent, Array.Empty<object>(), Array.Empty<object>()));
    }
    void ShareEditorMode(object sender, RoutedEventArgs e) => Run(() =>
    {
        EnsureValid();
        RefreshSharingSources();
        PublishKind.SelectedIndex = 1;
        PublishMode.SelectedValue = "__editor__";
        PublishTitle.Text = Configuration.Name(payload!);
        PublishAction.SelectedIndex = 0;
        SharedTab.IsSelected = true;
        LibraryPages.SelectedIndex = 1;
    });
    string ShareKind => (string)((ComboBoxItem)PublishKind.SelectedItem).Tag;
    void PublishKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PublishMode == null || PublishMap == null || PublishTitle == null)
            return;
        PublishMode.IsEnabled = ShareKind is "mode" or "bundle";
        PublishMap.IsEnabled = ShareKind is "map" or "bundle";
        PublishSourceChanged(sender, e);
    }
    void PublishSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingShareSources || PublishTitle == null || PublishKind?.SelectedItem == null)
            return;
        if (ShareKind == "mode" && PublishMode.SelectedItem is LocalShareSource mode)
            PublishTitle.Text = mode.Id == "__editor__" && payload != null ? Configuration.Name(payload) : mode.Name;
        if (ShareKind == "map" && PublishMap.SelectedItem is SavedMap map)
        {
            PublishTitle.Text = map.Name;
            if (PublishCredits.Text.Length == 0)
                PublishCredits.Text = map.Author;
        }
        if (ShareKind == "bundle" && (PublishTitle.Text.Length == 0 || PublishTitle.Text.StartsWith("Current editor:")))
            PublishTitle.Text = "New bundle";
    }
}
