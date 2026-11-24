using System.Windows;
using System.Windows.Controls;
using SGL.Core;
namespace SGL.Desktop;

public partial class MainWindow
{
    bool updatingModeSelection, updatingMapSelections;
    SavedMode[] SelectedModes => Installed?.SelectedItems.OfType<SavedMode>().ToArray() ?? [];
    SavedMap[] SelectedInstalledMaps => InstalledMaps?.SelectedItems.OfType<MapListItem>().Select(m => m.Installed).OfType<SavedMap>().ToArray() ?? [];
    CommunityMap[] SelectedCommunityMaps => SelectedLibraryRows.Select(r => r.Catalog).OfType<CommunityMap>().ToArray();

    void FilterModes()
    {
        if (Installed == null || snapshot == null) return;
        var selected = SelectedModes.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        updatingModeSelection = true;
        try
        {
            Installed.ItemsSource = snapshot.Modes.Where(m => m.Name.Contains(Search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var mode in Installed.Items.OfType<SavedMode>().Where(m => selected.Contains(m.Id)))
                Installed.SelectedItems.Add(mode);
        }
        finally { updatingModeSelection = false; }
        UpdateModeSelectionButtons();
    }
    void UpdateModeSelectionButtons()
    {
        if (DeleteModesButton == null) return;
        int count = SelectedModes.Length;
        ClearModesButton.IsEnabled = count > 1;
        DeleteModesButton.Content = count == 0 ? "Delete selected modes" : $"Delete selected ({count})";
        DeleteModesButton.IsEnabled = count > 0 && mapDownload == null;
    }
    void UpdateMapSelectionButtons()
    {
        if (DeleteMapsButton == null) return;
        int count = SelectedInstalledMaps.Length;
        ClearInstalledMapsButton.IsEnabled = count > 1;
        DeleteMapsButton.Content = count == 0 ? "Delete selected local maps" : $"Delete local maps ({count})";
        DeleteMapsButton.IsEnabled = count > 0 && mapDownload == null;
    }
    void RestoreMapSelection(ListBox list, IEnumerable<string> ids)
    {
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        updatingMapSelections = true;
        try
        {
            list.UnselectAll();
            foreach (var item in list.Items.OfType<MapListItem>().Where(m => selected.Contains(m.Id)))
                list.SelectedItems.Add(item);
        }
        finally { updatingMapSelections = false; }
    }
    void SelectAllModes(object sender, RoutedEventArgs e)
    {
        updatingModeSelection = true;
        try { Installed.SelectAll(); }
        finally { updatingModeSelection = false; }
        UpdateModeSelectionButtons();
    }
    void ClearModeSelection(object sender, RoutedEventArgs e) => Installed.UnselectAll();
    void SelectAllCommunityMaps(object sender, RoutedEventArgs e) => LibraryItems.SelectAll();
    void ClearCommunityMapSelection(object sender, RoutedEventArgs e) => LibraryItems.UnselectAll();
    void SelectAllInstalledMaps(object sender, RoutedEventArgs e) => InstalledMaps.SelectAll();
    void ClearInstalledMapSelection(object sender, RoutedEventArgs e) => InstalledMaps.UnselectAll();
}
