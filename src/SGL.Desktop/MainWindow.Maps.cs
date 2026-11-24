using System.Text.Json.Nodes;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SGL.Core;
namespace SGL.Desktop;

public partial class MainWindow
{
    CommunityMap[] mapCatalog = [];
    CancellationTokenSource? mapDownload, mapPreviewRequest;
    bool catalogLoaded, catalogLoading;
    string MapCache => Path.Combine(dataRoot, "Cache", "community-maps.json");
    MapLinks Links => new(Path.Combine(Path.GetDirectoryName(HistoryDirectory)!, "map-mode-links.json"));
    void RefreshMaps()
    {
        if (InstalledMaps == null || snapshot == null)
            return;
        string? currentMode = (MapModeChoice.SelectedItem as SavedMode)?.Id;
        mapTags = TagStore.Read();
        RefreshMapTagFilter();
        FilterInstalledMaps();
        FilterMaps();
        MapModeChoice.ItemsSource = snapshot.Modes.OrderBy(m => m.Name).ToArray();
        MapModeChoice.SelectedItem = snapshot.Modes.FirstOrDefault(m => m.Id == currentMode);
        RefreshMapLinks();
        UpdateMapInstallButton();
    }
    async void RefreshCatalogClick(object sender, RoutedEventArgs e) => await LoadCatalog(true);
    async Task LoadCatalog(bool refresh)
    {
        if (catalogLoading) return;
        catalogLoading = true;
        RefreshCatalogButton.IsEnabled = false;
        try
        {
            mapCatalog = await Maps.Catalog(MapCache, refresh);
            catalogLoaded = true;
            RefreshMapTagFilter(); FilterSharedItems();
        }
        catch (Exception error)
        {
            if (File.Exists(MapCache))
            {
                try { mapCatalog = await Maps.Catalog(MapCache); catalogLoaded = true; FilterSharedItems(); }
                catch { LibraryStatus.Text = "Cannot load the community catalog: " + error.Message; }
            }
            else LibraryStatus.Text = "Cannot load the community catalog: " + error.Message;
        }
        finally { catalogLoading = false; RefreshCatalogButton.IsEnabled = true; }
    }
    void FilterMaps() => FilterSharedItems();
    async void RefreshCommunitySelection(bool syncInstalled)
    {
        mapPreviewRequest?.Cancel();
        MapPreview.Source = null;
        MapPreview.Visibility = SelectedCommunityMap != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateMapInstallButton();
        if (SelectedCommunityMap is not CommunityMap map)
        {
            MapDetails.Text = $"{SelectedCommunityMaps.Length} catalog maps selected.";
            CatalogTags.Text = "";
            return;
        }
        MapDetails.Text = $"{map.Name}\nBy {map.Author} · {map.Category} · Architects catalog";
        CatalogTags.Text = TagSummary(map.Id);
        using var request = new CancellationTokenSource();
        mapPreviewRequest = request;
        try
        {
            byte[]? bytes = await Maps.Preview(map, request.Token);
            if (bytes == null || request.IsCancellationRequested || !ReferenceEquals(SelectedCommunityMap, map)) return;
            using var stream = new MemoryStream(bytes, false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 700; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            MapPreview.Source = bitmap;
        }
        catch { }
        finally { if (ReferenceEquals(mapPreviewRequest, request)) mapPreviewRequest = null; }
    }
    void UpdateMapInstallButton() { UpdateLibrarySelectionState(); UpdateMapSelectionButtons(); UpdateModeSelectionButtons(); }
    async void InstallMapClick(object sender, RoutedEventArgs e) => await InstallSelectedMaps();
    async Task InstallSelectedMaps(HttpMessageHandler? handler = null, Func<bool>? isGameRunning = null)
    {
        if (SelectedCommunityMaps.Length == 0 || store == null || snapshot == null || mapDownload != null)
            return;
        var selected = SelectedCommunityMaps.DistinctBy(m => m.Id).ToArray();
        var pending = selected.Where(m => !snapshot.Maps.Any(installed => installed.Id == m.Id)).ToArray();
        if (pending.Length == 0) return;
        int skipped = selected.Length - pending.Length, completed = 0;
        var target = store;
        var expected = snapshot;
        using var request = new CancellationTokenSource();
        mapDownload = request;
        UpdateNavigationBusy();
        LibraryCancelButton.IsEnabled = true;
        UpdateMapInstallButton();
        try
        {
            if ((isGameRunning ?? Modes.GameRunning)())
                throw new InvalidOperationException("Close Splitgate before installing a map.");
            foreach (var map in pending)
            {
                request.Token.ThrowIfCancellationRequested();
                int position = completed;
                MapStatus.Text = $"Downloading {position + 1}/{pending.Length}: {map.Name}…";
                var progress = new Progress<double>(n => { if (!request.IsCancellationRequested && ReferenceEquals(mapDownload, request) && completed == position) MapStatus.Text = $"Downloading {position + 1}/{pending.Length}: {map.Name} · {n:F0}%"; });
                byte[] bytes = await Maps.Download(map, request.Token, progress, handler);
                request.Token.ThrowIfCancellationRequested();
                if (store != target || snapshot != expected)
                    throw new IOException("Save folder or snapshot changed during download. Refresh before installing the remaining maps.");
                history?.Capture(expected, "Checkpoint before map installation");
                lastBackup = target.InstallMap(expected, map.Id, bytes, map.Name, map.Author);
                completed++;
                expected = target.Read();
                snapshot = expected;
                history?.Capture(expected, "Installed map: " + map.Name);
            }
            MapStatus.Text = $"Installed {completed} map(s); skipped {skipped} already installed. Latest backup: {lastBackup}";
        }
        catch (OperationCanceledException) { MapStatus.Text = $"Installation canceled. Installed {completed}/{pending.Length}; skipped {skipped} already installed. Completed installations were kept."; }
        catch (Exception error) { Report(error); MapStatus.Text = $"Installation stopped after {completed}/{pending.Length} installed; skipped {skipped} already installed. Completed installations were kept. " + error.Message; }
        finally
        {
            string result = MapStatus.Text;
            mapDownload = null;
            UpdateNavigationBusy();
            LibraryCancelButton.IsEnabled = false;
            if (store == target) Refresh(false);
            MapStatus.Text = result;
            UpdateMapInstallButton();
        }
    }
    void CancelMapClick(object sender, RoutedEventArgs e) => mapDownload?.Cancel();
    void ImportMapClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (mapDownload != null)
            throw new InvalidOperationException("Wait for the current map download or cancel it first.");
        if (store == null || snapshot == null)
            throw new IOException("Refresh the CloudSave folder first.");
        var dialog = new OpenFileDialog { Filter = "Splitgate map archive|*.bin", Title = "Import a Splitgate map" };
        if (dialog.ShowDialog(this) != true)
            return;
        if (new FileInfo(dialog.FileName).Length > Maps.MaximumArchive)
            throw new InvalidDataException("Map exceeds 128 MB.");
        byte[] bytes = File.ReadAllBytes(dialog.FileName);
        string id = Path.GetFileNameWithoutExtension(dialog.FileName);
        if (!Maps.ValidId(id))
            id = "import-" + Json.Hash(bytes)[..20].ToLowerInvariant();
        history?.Capture(snapshot, "Checkpoint before map import");
        lastBackup = store.InstallMap(snapshot, id, bytes);
        Refresh();
        SelectInstalledMap(id);
        MapStatus.Text = "Map imported. Backup: " + lastBackup;
    });
    void InstalledMapSelected(object sender, SelectionChangedEventArgs e)
    {
        if (updatingMapSelections) return;
        Run(UpdateInstalledMapDetails);
    }
    void UpdateInstalledMapDetails()
    {
        if (InstalledMapPreview == null) return;
        var map = snapshot?.Maps.FirstOrDefault(m => m.Id == SelectedInstalledMap?.Id);
        InstalledMapPreview.Map = map;
        InstalledMapPreviewPanel.Visibility = map != null ? Visibility.Visible : Visibility.Collapsed;
        RefreshMapLinks(); UpdateMapTagDetails(); UpdateMapSelectionButtons();
        MapLocalDetails.Text = map != null ? map.Name + "\n" + map.Author + "\n" + LocalOrigin("map", map.Id, map.ArchiveHash)
            : SelectedInstalledMaps.Length > 1 ? $"{SelectedInstalledMaps.Length} installed maps selected." : "Select one installed map to see its details.";
    }
    void RefreshMapLinks()
    {
        if (AssociatedModes == null)
            return;
        var ids = SelectedInstalledMap is SavedMap map ? Links.ModesFor(map.Id) : [];
        AssociatedModes.ItemsSource = snapshot?.Modes.Where(m => ids.Contains(m.Id)).OrderBy(m => m.Name).ToArray() ?? [];
    }
    void ChangeMapLink(bool associated) => Run(() =>
    {
        if (store == null || snapshot == null)
            throw new IOException("Refresh this CloudSave folder first.");
        if (SelectedInstalledMap is not SavedMap map || MapModeChoice.SelectedItem is not SavedMode mode)
            throw new InvalidDataException("Choose an installed map and a gamemode first.");
        Links.Set(map.Id, mode.Id, associated);
        RefreshMapLinks();
        MapStatus.Text = associated ? $"Linked {map.Name} with {mode.Name}. Choose both in the game's lobby." : "Association removed.";
    });
    void AssociateMapMode(object sender, RoutedEventArgs e) => ChangeMapLink(true);
    void UnlinkMapMode(object sender, RoutedEventArgs e) => ChangeMapLink(false);
    void OpenAssociatedMode(object sender, MouseButtonEventArgs e)
    {
        if (AssociatedModes.SelectedItem is SavedMode mode && CanLeave())
        {
            Load(mode.Payload, mode.Id);
            NavigateToContent("mode", "installed");
        }
    }
    void MapWebsiteClick(object sender, RoutedEventArgs e) => Run(() => OpenPath(Maps.Website));
}
