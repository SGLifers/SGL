using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SGL.Core;
namespace SGL.Desktop;

public partial class MainWindow
{
    sealed record ModBackupChoice(string Path, string Label);
    sealed record ModRow(CommunityMod Mod, InstalledMod? Local, string? Latest)
    {
        public string Name => Mod.Name;
        public string Status => Local == null ? "Available · " + Latest : (Local.Enabled ? "Enabled" : "Disabled") + " · " + Local.Version + (Latest != null && Mods.Newer(Latest, Local.Version) ? " · Update available" : "");
    }
    ModBundle? modBundle;
    ModStore? modStore;
    bool modsBusy, modsCatalogLoading, changingModSelection, modConfigLoading, modConfigDirty;
    string? selectedMod;
    ModConfiguration? openedModConfiguration;
    HttpMessageHandler? modsTransport = null;
    Func<bool>? modGameRunning = null;
    string ModCache => Path.Combine(dataRoot, "Mods", "Catalog");
    void InitializeMods()
    {
        string? saved = (string?)Preferences()["modGameFolder"];
        if (!string.IsNullOrWhiteSpace(saved)) { try { SetModGame(saved); } catch (Exception error) { ModsStatus.Text = error.Message; } }
        Loaded += async (_, _) =>
        {
            if (Arg("--no-mod-refresh") != null) return;
            if (modStore == null)
            {
                var found = await Task.Run(GameInstallation.Find);
                if (found.Length == 1) { try { SetModGame(found[0]); } catch (Exception error) { ModsStatus.Text = error.Message; } }
            }
            await DiscoverMods();
        };
        Closing += (_, e) =>
        {
            if (modsBusy) { e.Cancel = true; ModsStatus.Text = "Wait for the mod operation to finish before closing SGL."; }
            else if (!CanLeaveModConfiguration()) e.Cancel = true;
        };
        UpdateModDetails();
    }
    void SetModGame(string folder)
    {
        var next = new ModStore(folder, dataRoot, modGameRunning);
        modStore = next; ModGameFolder.Text = next.Root;
        var prefs = Preferences(); prefs["modGameFolder"] = next.Root;
        Json.Atomic(Path.Combine(dataRoot, "settings.json"), prefs.ToJsonString());
        RefreshModRows();
    }
    void BrowseModGame(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (modsBusy || !CanLeaveModConfiguration()) return;
        var dialog = new OpenFolderDialog { Title = "Choose the Splitgate game installation" };
        if (dialog.ShowDialog(this) == true) SetModGame(dialog.FolderName);
    });
    void FindModGame(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (modsBusy || !CanLeaveModConfiguration()) return;
        var folders = GameInstallation.Find();
        if (folders.Length == 1) { SetModGame(folders[0]); ModsStatus.Text = "Found Splitgate in Steam."; }
        else ModsStatus.Text = folders.Length == 0 ? "No Steam installation found. Browse to your Splitgate game folder." : "Multiple Splitgate installations found. Use Browse to choose the one you host with.";
        Status.Text = ModsStatus.Text;
    });
    async Task DiscoverMods()
    {
        if (modsCatalogLoading || modsBusy) return;
        modsCatalogLoading = true; RefreshModsButton.IsEnabled = false;
        try
        {
            ModsStatus.Text = "Checking published community mods…";
            modBundle = await Mods.Catalog(ModCache, true, handler: modsTransport);
            ModsStatus.Text = $"{modBundle.Release.Mods.Length} mods · {modBundle.Release.Version}. New mods remain disabled until you enable them.";
        }
        catch (Exception error)
        {
            try
            {
                modBundle = await Mods.Catalog(ModCache, false, handler: modsTransport);
                ModsStatus.Text = "Offline catalog · " + modBundle.Release.Version + ". Refresh unavailable: " + error.Message;
            }
            catch { ModsStatus.Text = "Mod discovery unavailable. Installed mods still work. " + error.Message; }
        }
        finally { modsCatalogLoading = false; RefreshModsButton.IsEnabled = !modsBusy; RefreshModRows(preserveConfiguration: true); }
    }
    async void RefreshModsClick(object sender, RoutedEventArgs e) => await DiscoverMods();
    void ModSearchChanged(object sender, TextChangedEventArgs e) { if (ModList != null && !modsBusy) RefreshModRows(true); }
    void RefreshModRows(bool preserveConfiguration = false)
    {
        try
        {
            var installed = modStore?.Installed() ?? [];
            var catalog = modBundle?.Release.Mods.ToList() ?? [];
            foreach (var item in installed.Where(i => catalog.All(m => m.Id != i.Id))) catalog.Add(new(item.Id, item.Id, "Installed mod absent from the current catalog. Its configuration and disable action remain available.", "See mod documentation for requirements"));
            string query = ModSearch.Text.Trim();
            var rows = catalog.Select(m => new ModRow(m, installed.SingleOrDefault(i => i.Id == m.Id), modBundle?.Release.Mods.Any(c => c.Id == m.Id) == true ? modBundle.Release.Version : null))
                .Where(r => (r.Name + " " + r.Mod.Id + " " + r.Mod.Description).Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            changingModSelection = true;
            ModList.ItemsSource = rows;
            ModList.SelectedItem = rows.FirstOrDefault(r => r.Mod.Id == selectedMod);
            changingModSelection = false;
            if (!preserveConfiguration || !modConfigDirty) UpdateModDetails();
            else UpdateModButtons();
        }
        catch (Exception error) { changingModSelection = false; ModsStatus.Text = error.Message; }
    }
    bool CanLeaveModConfiguration() => !modConfigDirty || Confirm("Discard the unsaved mod configuration changes?");
    void ModSelected(object sender, SelectionChangedEventArgs e)
    {
        if (changingModSelection) return;
        if (!CanLeaveModConfiguration())
        {
            changingModSelection = true; ModList.SelectedItem = ModList.Items.Cast<ModRow>().FirstOrDefault(r => r.Mod.Id == selectedMod); changingModSelection = false; return;
        }
        selectedMod = (ModList.SelectedItem as ModRow)?.Mod.Id;
        UpdateModDetails();
    }
    void UpdateModDetails()
    {
        var row = ModList.SelectedItem as ModRow;
        selectedMod = row?.Mod.Id;
        ModTitle.Text = row?.Name ?? "Select a mod";
        ModDescription.Text = row?.Mod.Description ?? "Community mods are downloaded from Splitgate Architects releases.";
        ModScope.Text = row?.Mod.Scope ?? "";
        ModRuntimeStatus.Text = modStore == null ? "Choose the game folder to enable mods." : modStore.RuntimeInstalled
            ? "Managed UE4SS 4.0.0 installed. Backups are kept separately from game saves."
            : "Enabling the first mod installs compatible UE4SS automatically. Existing version.dll files are preserved.";
        modConfigLoading = true;
        try
        {
            openedModConfiguration = null; modConfigDirty = false; ModConfigText.Clear();
            var configs = row?.Local != null ? modStore!.Configurations(row.Mod.Id).OrderBy(c => c.Path.EndsWith("/config.json", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(c => c.Path).ToArray() : [];
            ModConfigFiles.ItemsSource = configs; ModConfigFiles.SelectedIndex = configs.Length > 0 ? 0 : -1;
            ModConfigurationPanel.Visibility = configs.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (configs.Length > 0) LoadModConfiguration(configs[0]);
        }
        finally { modConfigLoading = false; }
        var backups = (modStore?.Backups() ?? []).Select(path =>
        {
            try
            {
                var record = SGL.Core.Json.Read(File.ReadAllText(Path.Combine(path, "backup.json")));
                return (string?)record["status"] == "complete" ? new ModBackupChoice(path, ((string?)record["action"] ?? "Mod operation") + " · " + Path.GetFileName(path)) : null;
            }
            catch { return null; }
        }).OfType<ModBackupChoice>().ToArray();
        ModBackups.ItemsSource = backups; ModBackups.SelectedIndex = backups.Length > 0 ? 0 : -1;
        UpdateModButtons();
    }
    void UpdateModButtons()
    {
        var row = ModList.SelectedItem as ModRow;
        bool available = !modsBusy && modStore != null && row != null;
        EnableModButton.Content = row?.Local == null ? "Install & enable" : "Enable";
        EnableModButton.IsEnabled = available && !modConfigDirty && row?.Local?.Enabled != true && (row?.Local != null || row?.Latest != null);
        DisableModButton.IsEnabled = available && !modConfigDirty && row?.Local?.Enabled == true;
        UpdateModButton.IsEnabled = available && !modConfigDirty && row?.Local != null && row.Latest != null && Mods.Newer(row.Latest, row.Local.Version);
        SaveModConfigButton.IsEnabled = available && modConfigDirty && openedModConfiguration != null;
        OpenModFolderButton.IsEnabled = available && row?.Local != null;
        RestoreModBackupButton.IsEnabled = !modsBusy && !modConfigDirty && modStore != null && ModBackups.Items.Count > 0;
        OpenModBackupsButton.IsEnabled = !modsBusy && modStore != null && modStore.Backups().Length > 0;
        RefreshModsButton.IsEnabled = !modsBusy && !modsCatalogLoading;
    }
    async Task ModOperation(Func<ModStore, Task<string>> action)
    {
        if (modsBusy || modStore == null) return;
        if ((modGameRunning ?? Modes.GameRunning)()) { ModsStatus.Text = "Close Splitgate before changing mods."; return; }
        modsBusy = true; ModsWorkspace.IsEnabled = false; ModGameFolder.Parent.SetValue(IsEnabledProperty, false);
        UpdateNavigationBusy(); UpdateModButtons();
        try
        {
            ModsStatus.Text = "Preparing mod operation…";
            string backup = await action(modStore);
            modConfigDirty = false;
            ModsStatus.Text = "Saved. Launch Splitgate to apply. Backup: " + backup;
        }
        catch (Exception error) { ModsStatus.Text = error.Message; }
        finally
        {
            modsBusy = false; ModsWorkspace.IsEnabled = true; ModGameFolder.Parent.SetValue(IsEnabledProperty, true);
            RefreshModRows(preserveConfiguration: true); UpdateNavigationBusy(); UpdateModButtons();
        }
    }
    async Task InstallSelectedMod()
    {
        if (selectedMod == null || modBundle == null) return;
        string id = selectedMod; var bundle = modBundle;
        await ModOperation(async target =>
        {
            ModsStatus.Text = target.RuntimeInstalled ? "Installing mod update; preserving configurations…" : "Preparing verified UE4SS and Splitgate compatibility patches…";
            var runtime = target.RuntimeInstalled ? null : await ModRuntime.Prepare(ModCache, handler: modsTransport);
            return await Task.Run(() => target.Install(bundle, id, runtime));
        });
    }
    async void EnableModClick(object sender, RoutedEventArgs e)
    {
        if (selectedMod == null || modStore == null) return;
        if (!modStore.Installed().Any(m => m.Id == selectedMod)) await InstallSelectedMod();
        else { string id = selectedMod; await ModOperation(store => Task.Run(() => store.Toggle(id, true))); }
    }
    async void DisableModClick(object sender, RoutedEventArgs e)
    {
        if (selectedMod is string id) await ModOperation(store => Task.Run(() => store.Toggle(id, false)));
    }
    async void UpdateModClick(object sender, RoutedEventArgs e) => await InstallSelectedMod();
    void LoadModConfiguration(ModConfiguration config)
    {
        modConfigLoading = true; openedModConfiguration = config; ModConfigText.Text = config.Text; modConfigDirty = false;
        ModConfigHint.Text = selectedMod == "MapEditorBudgetMod" ? "Change BudgetLimit in the returned table. The mod supports F7 reload; SGL saves with the game closed." : "Edit JSON values, phrase lists or bot names. Configuration changes are backed up before saving.";
        modConfigLoading = false; UpdateModButtons();
    }
    void ModConfigSelected(object sender, SelectionChangedEventArgs e)
    {
        if (modConfigLoading) return;
        if (!CanLeaveModConfiguration()) { modConfigLoading = true; ModConfigFiles.SelectedItem = openedModConfiguration; modConfigLoading = false; return; }
        if (ModConfigFiles.SelectedItem is ModConfiguration config) LoadModConfiguration(config);
    }
    void ModConfigurationChanged(object sender, TextChangedEventArgs e)
    {
        if (modConfigLoading || SaveModConfigButton == null) return;
        modConfigDirty = openedModConfiguration != null && ModConfigText.Text != openedModConfiguration.Text; UpdateModButtons();
    }
    async void SaveModConfiguration(object sender, RoutedEventArgs e)
    {
        if (selectedMod is not string id || openedModConfiguration is not ModConfiguration config) return;
        string text = ModConfigText.Text;
        await ModOperation(store => Task.Run(() => store.Configure(id, config, text)));
    }
    void ReloadModConfiguration(object sender, RoutedEventArgs e) => Run(() => { if (!modsBusy && CanLeaveModConfiguration()) UpdateModDetails(); });
    async void RestoreModBackup(object sender, RoutedEventArgs e)
    {
        if (ModBackups.SelectedItem is ModBackupChoice backup && CanLeaveModConfiguration() && Confirm("Restore this mod operation? Restore newer operations first. A safety backup is created, and changed files are never overwritten."))
            await ModOperation(store => Task.Run(() => store.Restore(backup.Path)));
    }
    void OpenModsDocs(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://github.com/" + Mods.Repository) { UseShellExecute = true });
    void OpenModBackups(object sender, RoutedEventArgs e) => Run(() => { if (modStore != null && Directory.Exists(modStore.BackupDirectory)) Process.Start(new ProcessStartInfo(modStore.BackupDirectory) { UseShellExecute = true }); });
    void OpenModFolder(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (modStore != null && selectedMod != null)
        {
            string path = GameStore.Inside(modStore.Root, "ue4ss/Mods/" + selectedMod);
            if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    });
}
