using System.Windows;
using SGL.Core;
namespace SGL.Desktop;

public partial class MainWindow
{
    void DeleteModeClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var modes = SelectedModes;
        if (store == null || snapshot == null || modes.Length == 0 || history == null)
            throw new InvalidOperationException("Select installed gamemodes and refresh the save folder first.");
        if (Modes.GameRunning())
            throw new InvalidOperationException("Close Splitgate before deleting a gamemode.");
        if (mapDownload != null)
            throw new InvalidOperationException("Wait for the map download or cancel it before deleting content.");
        bool deletingOpenedMode = modes.Any(m => m.Id == selectedId);
        if ((deletingOpenedMode && !CanLeave()) || !Confirm($"Delete {modes.Length} gamemode(s) from this machine?\n\n{SelectionNames(modes.Select(m => m.Name))}\n\nAll their saved versions will be moved into one backup. You can restore the backup or recover modes from History."))
            return;
        history.Capture(snapshot, $"Checkpoint before deleting {modes.Length} gamemode(s)");
        var result = store.DeleteMany(snapshot, "CustomModes", modes.Select(m => m.Id));
        if (deletingOpenedMode) { dirty = false; invalid.Clear(); }
        history.Capture(store.Read(), $"Deleted {modes.Length} gamemode(s): " + string.Join(", ", modes.Select(m => m.Name)));
        Refresh();
        if (deletingOpenedMode) NewDocument(((BaseMode)Base.SelectedItem).Id);
        ShowDeletionResult($"{modes.Length} gamemode(s)", result);
    });
    void DeleteMapClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var maps = SelectedInstalledMaps;
        if (store == null || snapshot == null || maps.Length == 0)
            throw new InvalidOperationException("Select installed maps first.");
        if (Modes.GameRunning())
            throw new InvalidOperationException("Close Splitgate before deleting a map.");
        if (mapDownload != null)
            throw new InvalidOperationException("Wait for the map download or cancel it before deleting content.");
        if (!Confirm($"Delete {maps.Length} map(s) from this machine?\n\n{SelectionNames(maps.Select(m => m.Name))}\n\nThis includes their Map Lab edits and older saves. The map files will be moved into one backup. Associated gamemodes will remain installed."))
            return;
        var result = store.DeleteMany(snapshot, "MapCreator", maps.Select(m => m.Id));
        Refresh();
        ShowDeletionResult($"{maps.Length} map(s)", result);
        UpdateMapInstallButton();
    });
    static string SelectionNames(IEnumerable<string> names)
    {
        var all = names.ToArray();
        return string.Join("\n", all.Take(15).Select(n => "• " + n)) + (all.Length > 15 ? $"\n… and {all.Length - 15} more" : "");
    }
    void ShowDeletionResult(string name, DeletionResult result)
    {
        lastBackup = result.BackupDirectory;
        string text = "Deleted " + name + ". Backup: " + result.BackupDirectory;
        if (result.CleanupIssues.Count > 0)
        {
            text += "\nThe item was removed from the game index, but some files could not be moved. Do not run MapLoader until these files are handled:\n" + string.Join("\n", result.CleanupIssues);
            MessageBox.Show(this, text, "SGL · deletion cleanup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Status.Text = text;
        MapStatus.Text = text;
    }
}
