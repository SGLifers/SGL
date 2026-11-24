using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using SGL.Core;
namespace SGL.Desktop;

public partial class MainWindow
{
    void LibrarySourceChanged(object sender, SelectionChangedEventArgs e) { if (!filteringSharedItems) FilterSharedItems(); }
    string RemoteStatus(LibraryItem item, IReadOnlyList<JsonObject> records, string? account)
    {
        var record = records.FirstOrDefault(r => (string?)r["itemId"] == item.Id);
        record ??= item.OwnerId == account ? SharedState.PublicationForItem(LibraryRoot.TrimEnd('/') + "/#" + account, item.Id) : null;
        string? id = (string?)record?["localFileId"];
        string? hash = item.Kind == "mode" ? snapshot?.Modes.FirstOrDefault(m => m.Id == id)?.ArchiveHash : snapshot?.Maps.FirstOrDefault(m => m.Id == id)?.ArchiveHash;
        var labels = new List<string> { hash == null ? item.Kind == "bundle" ? "Paired map + gamemode" : "Available to install" : "Installed" };
        if (hash != null && record?["archiveHash"] != null && hash != (string?)record?["archiveHash"]) labels.Add("Local edits");
        if (hash != null && (string?)record?["revisionId"] != item.LatestRevisionId) labels.Add("Update available");
        if (item.OwnerId == account) labels.Add("Your upload");
        if (BrowsingMine && item.Json["visibility"] is JsonValue visibility) labels.Add(visibility.ToString());
        return string.Join(" · ", labels);
    }
    void FilterSharedItems()
    {
        if (filteringSharedItems || LibraryItems == null || LibrarySearch == null || LibrarySource == null) return;
        var selected = SelectedLibraryRows.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var records = SharedState.Installations(LibraryRoot);
        string? account = SignedInUser;
        IEnumerable<LibraryBrowseEntry> rows = sharedItems.Where(i => LibraryBrowseEntry.MatchesKind(i, browseKind) && (!BrowsingMine || i.OwnerId == account))
            .Select(i => new LibraryBrowseEntry(i, null, RemoteStatus(i, records, account), i.Tags));
        if (browseKind == "map" && !BrowsingMine)
            rows = rows.Concat(mapCatalog.Select(m => new LibraryBrowseEntry(null, m, snapshot?.Maps.Any(i => i.Id == m.Id) == true ? "Installed" : "Available to install", TagsFor(m.Id))));
        string source = BrowsingMine || browseKind != "map" ? "sgl" : (string?)((ComboBoxItem?)LibrarySource.SelectedItem)?.Tag ?? "all";
        var matches = rows.Where(r => r.Matches(LibrarySearch.Text.Trim(), appliedLibraryTag, appliedLibraryCreator, source)).DistinctBy(r => r.Key).OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToArray();
        filteringSharedItems = true;
        try
        {
            LibraryItems.ItemsSource = matches;
            foreach (var row in matches.Where(r => selected.Contains(r.Key))) LibraryItems.SelectedItems.Add(row);
        }
        finally { filteringSharedItems = false; }
        UpdateLibrarySelectionState();
    }
    void UpdateLibrarySelectionState()
    {
        if (ClearLibrarySelectionButton == null) return;
        var rows = SelectedLibraryRows;
        EditCatalogTagsButton.IsEnabled = SelectedCommunityMap != null;
        var publication = SelectedLibraryItem;
        var tracking = publication == null ? null : SharedState.Installed(LibraryRoot, publication.Id) ?? OwnPublicationRecord(publication);
        string? local = (string?)tracking?["localFileId"];
        OpenInstalledPublicationButton.IsEnabled = !libraryBusy && publication != null && (publication.Kind == "mode" ? snapshot?.Modes.Any(m => m.Id == local) == true : publication.Kind == "map" && snapshot?.Maps.Any(m => m.Id == local) == true);
        UpdatePublicationButton.IsEnabled = !libraryBusy && publication?.OwnerId == SignedInUser && publication != null && LibraryRevisions.SelectedItem != null;
        ForkPublicationButton.IsEnabled = !libraryBusy && publication != null && publication.OwnerId != SignedInUser && LibraryRevisions.SelectedItem != null;
        int pending = rows.Count(r => r.Catalog == null || snapshot?.Maps.Any(m => m.Id == r.Catalog.Id) != true);
        ClearLibrarySelectionButton.IsEnabled = rows.Length > 1;
        InstallLibrarySelectionButton.Content = rows.Length == 0 ? "Install selected" : pending == 0 ? "Already installed" : $"Install selected ({pending})";
        InstallLibrarySelectionButton.IsEnabled = pending > 0 && !libraryBusy && mapDownload == null && store != null && snapshot != null;
        CatalogDetailsPanel.Visibility = rows.Length > 0 && rows.All(r => r.Catalog != null) ? Visibility.Visible : Visibility.Collapsed;
        SharedDetailsPanel.Visibility = SelectedLibraryItem != null ? Visibility.Visible : Visibility.Collapsed;
        if (SelectedLibraryItem == null)
        {
            libraryPreviewRequest?.Cancel();
            LibraryMapPreview.PreviewDescriptor = null;
            LibraryMapPreviewPanel.Visibility = Visibility.Collapsed;
        }
    }
    void SelectAllLibraryItems(object sender, RoutedEventArgs e) => LibraryItems.SelectAll();
    void ClearLibrarySelection(object sender, RoutedEventArgs e) => LibraryItems.UnselectAll();
    async void InstallLibrarySelection(object sender, RoutedEventArgs e)
    {
        if (libraryBusy || mapDownload != null) return;
        if (SelectedLibraryRows.Length > 0 && SelectedLibraryRows.All(r => r.Catalog != null)) { await InstallSelectedMaps(); return; }
        await LibraryWork(async cancel =>
    {
        if (store == null || snapshot == null) throw new InvalidOperationException("Refresh your game saves first.");
        var rows = SelectedLibraryRows;
        if (rows.Length == 0) return;
        if (!Confirm($"Install {rows.Length} selected item(s)?\n\n{SelectionNames(rows.Select(r => r.Title))}\n\nA complete backup is made before each installation. Existing catalog maps are skipped and local edits are preserved.")) return;
        int completed = 0, skipped = 0;
        var target = store; var expected = snapshot;
        using var client = SharingClient();
        try
        {
            foreach (var row in rows)
            {
                cancel.ThrowIfCancellationRequested();
                LibraryStatus.Text = $"Installing {completed + skipped + 1}/{rows.Length}: {row.Title}…";
                if (row.Catalog is CommunityMap map)
                {
                    if (expected.Maps.Any(m => m.Id == map.Id)) { skipped++; continue; }
                    byte[] bytes = await Maps.Download(map, cancel);
                    cancel.ThrowIfCancellationRequested();
                    if (store != target || snapshot != expected) throw new IOException("The save folder changed during download. Refresh and retry.");
                    history?.Capture(expected, "Checkpoint before community map installation");
                    lastBackup = target.InstallMap(expected, map.Id, bytes, map.Name, map.Author);
                    expected = target.Read(); snapshot = expected; completed++;
                }
                else if (row.Publication is LibraryItem item)
                {
                    string revision = rows.Length == 1 && LibraryRevisions.SelectedItem is LibraryRevision selected ? selected.Id : item.LatestRevisionId;
                    var package = await client.Download(item.Id, revision, cancel);
                    cancel.ThrowIfCancellationRequested();
                    if (store != target || snapshot != expected) throw new IOException("The save folder changed during download. Refresh and retry.");
                    InstallSharedPackage(package, client.Root.AbsoluteUri, false);
                    target = store!; expected = snapshot!; completed++;
                }
            }
            LibraryStatus.Text = $"Installed {completed}; skipped {skipped} already installed.";
        }
        catch (OperationCanceledException) { LibraryStatus.Text = $"Canceled. Installed {completed}; skipped {skipped}. Completed installations were kept."; }
        catch (Exception error) { throw new IOException($"Stopped after {completed} installed and {skipped} skipped. Completed installations were kept. " + error.Message, error); }
        finally { string result = LibraryStatus.Text; if (store == target) Refresh(false); LibraryStatus.Text = result; }
        });
    }
    string LocalOrigin(string kind, string id, string currentHash)
    {
        var origin = SharedState.OriginForLocal(LibraryRoot, kind, id);
        if (origin == null) return "On this PC";
        string creator = (string?)origin["attribution"]?["item"]?["owner"]?["displayName"] ?? "Community";
        return "On this PC · " + creator + (currentHash != (string?)origin["archiveHash"] ? " · Local edits" : "");
    }
    void ModeOriginLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock text && text.DataContext is SavedMode mode) text.Text = LocalOrigin("mode", mode.Id, mode.ArchiveHash);
    }
}
