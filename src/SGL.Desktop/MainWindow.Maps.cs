using System.Text.Json.Nodes;
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
        string? currentMap = (InstalledMaps.SelectedItem as SavedMap)?.Id;
        string? currentMode = (MapModeChoice.SelectedItem as SavedMode)?.Id;
        InstalledMaps.ItemsSource = snapshot.Maps.OrderBy(m => m.Name).ToArray();
        InstalledMaps.SelectedItem = snapshot.Maps.FirstOrDefault(m => m.Id == currentMap);
        MapModeChoice.ItemsSource = snapshot.Modes.OrderBy(m => m.Name).ToArray();
        MapModeChoice.SelectedItem = snapshot.Modes.FirstOrDefault(m => m.Id == currentMode);
        RefreshMapLinks();
        UpdateMapInstallButton();
    }
    async void MapsTabSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, Tabs) && MapsTab.IsSelected && !catalogLoaded && !catalogLoading)
            await LoadCatalog(false);
    }
    async void RefreshCatalogClick(object sender, RoutedEventArgs e) => await LoadCatalog(true);
    async Task LoadCatalog(bool refresh)
    {
        if (catalogLoading)
            return;
        catalogLoading = true;
        RefreshCatalogButton.IsEnabled = false;
        try
        {
            MapStatus.Text = "Loading community map catalog…";
            mapCatalog = await Maps.Catalog(MapCache, refresh);
            catalogLoaded = true;
            string? category = MapCategory.SelectedItem as string;
            MapCategory.ItemsSource = new[] { "All" }.Concat(mapCatalog.Select(m => m.Category).Distinct().Order()).ToArray();
            MapCategory.SelectedItem = category ?? "All";
            FilterMaps();
            MapStatus.Text = $"{mapCatalog.Length:N0} community maps. Catalog cached for offline browsing; installation downloads the selected map.";
        }
        catch (Exception error)
        {
            if (File.Exists(MapCache))
            {
                try
                {
                    mapCatalog = await Maps.Catalog(MapCache);
                    catalogLoaded = true;
                    MapCategory.ItemsSource = new[] { "All" }.Concat(mapCatalog.Select(m => m.Category).Distinct().Order()).ToArray();
                    MapCategory.SelectedItem = "All";
                    FilterMaps();
                    MapStatus.Text = "Using cached catalog. Refresh failed: " + error.Message;
                }
                catch { MapStatus.Text = "Cannot load map catalog: " + error.Message; }
            }
            else
                MapStatus.Text = "Cannot load map catalog: " + error.Message;
        }
        finally { catalogLoading = false; RefreshCatalogButton.IsEnabled = true; }
    }
    void MapFilterChanged(object sender, SelectionChangedEventArgs e) => FilterMaps();
    void MapSearchChanged(object sender, TextChangedEventArgs e) => FilterMaps();
    void FilterMaps()
    {
        if (CommunityMaps == null || MapSearch == null)
            return;
        string search = MapSearch.Text.Trim(), category = MapCategory.SelectedItem as string ?? "All";
        CommunityMaps.ItemsSource = mapCatalog.Where(m => (category == "All" || m.Category == category) && (m.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || m.Author.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
    }
    async void CommunityMapSelected(object sender, SelectionChangedEventArgs e)
    {
        mapPreviewRequest?.Cancel();
        MapPreview.Source = null;
        UpdateMapInstallButton();
        if (CommunityMaps.SelectedItem is not CommunityMap map)
        {
            MapDetails.Text = "Select a community map.";
            return;
        }
        MapDetails.Text = $"{map.Name}\nBy {map.Author} · {map.Category}";
        var installed = snapshot?.Maps.FirstOrDefault(m => m.Id == map.Id);
        InstalledMaps.SelectedItem = installed;
        using var request = new CancellationTokenSource();
        mapPreviewRequest = request;
        try
        {
            byte[]? bytes = await Maps.Preview(map, request.Token);
            if (bytes == null || request.IsCancellationRequested || !ReferenceEquals(CommunityMaps.SelectedItem, map))
                return;
            using var stream = new MemoryStream(bytes, false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 700;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            MapPreview.Source = bitmap;
        }
        catch { /* A missing preview does not prevent map installation. */ }
        finally { if (ReferenceEquals(mapPreviewRequest, request)) mapPreviewRequest = null; }
    }
    void UpdateMapInstallButton()
    {
        if (InstallMapButton == null)
            return;
        bool installed = CommunityMaps.SelectedItem is CommunityMap map && snapshot?.Maps.Any(m => m.Id == map.Id) == true;
        InstallMapButton.Content = installed ? "Already installed" : "Install selected map";
        InstallMapButton.IsEnabled = mapDownload == null && !installed && CommunityMaps.SelectedItem is CommunityMap && store != null && snapshot != null;
    }
    async void InstallMapClick(object sender, RoutedEventArgs e)
    {
        if (CommunityMaps.SelectedItem is not CommunityMap map || store == null || snapshot == null || mapDownload != null)
            return;
        var target = store;
        var expected = snapshot;
        using var request = new CancellationTokenSource();
        mapDownload = request;
        CancelMapButton.IsEnabled = true;
        UpdateMapInstallButton();
        try
        {
            if (Modes.GameRunning())
                throw new InvalidOperationException("Close Splitgate before installing a map.");
            MapStatus.Text = "Downloading " + map.Name + "…";
            var progress = new Progress<double>(n => { if (!request.IsCancellationRequested) MapStatus.Text = $"Downloading {map.Name} · {n:F0}%"; });
            byte[] bytes = await Maps.Download(map, request.Token, progress);
            request.Token.ThrowIfCancellationRequested();
            if (store != target || snapshot != expected)
                throw new IOException("Save folder or snapshot changed during download. Select Install again after refreshing.");
            history?.Capture(expected, "Checkpoint before map installation");
            lastBackup = target.InstallMap(expected, map.Id, bytes, map.Name, map.Author);
            Refresh();
            InstalledMaps.SelectedItem = snapshot?.Maps.FirstOrDefault(m => m.Id == map.Id);
            MapStatus.Text = "Installed " + map.Name + ". Open the game's custom lobby and choose it from your maps. Backup: " + lastBackup;
        }
        catch (OperationCanceledException) { MapStatus.Text = "Map download canceled. Game saves were not changed."; }
        catch (Exception error) { Report(error); MapStatus.Text = error.Message; }
        finally { mapDownload = null; CancelMapButton.IsEnabled = false; UpdateMapInstallButton(); }
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
        InstalledMaps.SelectedItem = snapshot?.Maps.FirstOrDefault(m => m.Id == id);
        MapStatus.Text = "Map imported. Backup: " + lastBackup;
    });
    void InstalledMapSelected(object sender, SelectionChangedEventArgs e) => Run(RefreshMapLinks);
    void RefreshMapLinks()
    {
        if (AssociatedModes == null)
            return;
        var ids = InstalledMaps.SelectedItem is SavedMap map ? Links.ModesFor(map.Id) : [];
        AssociatedModes.ItemsSource = snapshot?.Modes.Where(m => ids.Contains(m.Id)).OrderBy(m => m.Name).ToArray() ?? [];
    }
    void ChangeMapLink(bool associated) => Run(() =>
    {
        if (store == null || snapshot == null)
            throw new IOException("Refresh this CloudSave folder first.");
        if (InstalledMaps.SelectedItem is not SavedMap map || MapModeChoice.SelectedItem is not SavedMode mode)
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
            Tabs.SelectedIndex = 0;
        }
    }
    void MapWebsiteClick(object sender, RoutedEventArgs e) => Run(() => OpenPath(Maps.Website));
}
