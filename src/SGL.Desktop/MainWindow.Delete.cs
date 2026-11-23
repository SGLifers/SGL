using System.Windows;
using SGL.Core;
namespace SGL.Desktop;

public partial class MainWindow
{
    void DeleteModeClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (store == null || snapshot == null || Installed.SelectedItem is not SavedMode mode)
            throw new InvalidOperationException("Select an installed gamemode first.");
        if (Modes.GameRunning())
            throw new InvalidOperationException("Close Splitgate before deleting a gamemode.");
        if (mapDownload != null)
            throw new InvalidOperationException("Wait for the map download or cancel it before deleting content.");
        if (!CanLeave() || !Confirm($"Delete “{mode.Name}” from this machine?\n\nAll its saved versions will be moved into a backup. You can restore the backup or recover the mode from History."))
            return;
        history!.Capture(snapshot, "Checkpoint before deleting " + mode.Name);
        var result = store.Delete(snapshot, "CustomModes", mode.Id);
        dirty = false;
        invalid.Clear();
        history.Capture(store.Read(), "Deleted gamemode: " + mode.Name);
        Refresh();
        NewDocument((string)((System.Windows.Controls.ComboBoxItem)Base.SelectedItem).Tag);
        ShowDeletionResult(mode.Name, result);
    });
    void DeleteMapClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (store == null || snapshot == null || SelectedInstalledMap is not SavedMap map)
            throw new InvalidOperationException("Select an installed map first.");
        if (Modes.GameRunning())
            throw new InvalidOperationException("Close Splitgate before deleting a map.");
        if (mapDownload != null)
            throw new InvalidOperationException("Wait for the map download or cancel it before deleting content.");
        if (!Confirm($"Delete “{map.Name}” from this machine?\n\nThis includes its Map Lab edits and older saves. The map files will be moved into a backup. Associated gamemodes will remain installed."))
            return;
        var result = store.Delete(snapshot, "MapCreator", map.Id);
        Refresh();
        ShowDeletionResult(map.Name, result);
        UpdateMapInstallButton();
    });
    void ShowDeletionResult(string name, DeletionResult result)
    {
        lastBackup = result.BackupDirectory;
        string text = "Deleted “" + name + "”. Backup: " + result.BackupDirectory;
        if (result.CleanupIssues.Count > 0)
        {
            text += "\nThe item was removed from the game index, but some files could not be moved. Do not run MapLoader until these files are handled:\n" + string.Join("\n", result.CleanupIssues);
            MessageBox.Show(this, text, "SGL · deletion cleanup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Status.Text = text;
        MapStatus.Text = text;
    }
}
