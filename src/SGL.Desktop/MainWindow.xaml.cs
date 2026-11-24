using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation;
using Microsoft.Win32;
using SGL.Core;
namespace SGL.Desktop;

public partial class MainWindow : Window
{
    readonly string dataRoot; readonly string[] args; readonly Dictionary<string, string> invalid = [];
    GameStore? store; GameSnapshot? snapshot; History? history; JsonObject? payload, opened; string? selectedId, openedArchiveHash; bool loading, dirty; string? lastBackup;
    bool changingSettingsCategory;
    string? ruleContextId;
    RuleContext CurrentRule => new(ruleContextId ?? Configuration.Rules(payload!)[0].Id, (RulePhase.SelectedItem as RuleContext)?.Name ?? "Standard Rules");
    void RulePhaseChanged(object s, SelectionChangedEventArgs e)
    {
        if (loading || payload == null || RulePhase.SelectedItem is not RuleContext rule) return;
        if (invalid.Count > 0)
        {
            loading = true; RulePhase.SelectedValue = ruleContextId; loading = false;
            Status.Text = "Correct invalid values before switching rule phase."; return;
        }
        ruleContextId = rule.Id; Render(); SettingsScroll?.ScrollToTop();
    }
    string settingsCategoryBeforeSearch = "Players";
    string HistoryDirectory => Path.Combine(dataRoot, "Profiles", Json.Hash(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(Folder.Text).ToUpperInvariant()))[..16], "History");
    public MainWindow(string[] args)
    {
        this.args = args;
        dataRoot = Arg("--data") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SGL");
        Directory.CreateDirectory(dataRoot);
        InitializeComponent();
        InitializeLoadoutTools();
        Base.ItemsSource = BaseModes.All;
        Base.SelectedValue = "koth";
        loading = true;
        var prefs = Preferences();
        RestorePaneWidths(prefs);
        Folder.Text = Arg("--cloud-save") ?? (string?)prefs["cloudSave"] ?? GameStore.DefaultRoot;
        AutoUpdate.IsChecked = (bool?)prefs["autoUpdate"] ?? true;
        loading = false;
        InitializeSharing();
        InitializeContentNavigation();
        InitializeMods();
        InitializeHosting();
        SizeChanged += (s, e) => ApplyPaneWidths();
        ModePanes.SizeChanged += (s, e) => ApplyPaneWidths();
        SettingsPanes.SizeChanged += (s, e) => ApplyPaneWidths();
        MapPanes.SizeChanged += (s, e) => ApplyPaneWidths();
        Folder.TextChanged += (s, e) => { store = null; snapshot = null; UpdateMapTagDetails(); UpdateInstalledMapDetails(); Status.Text = "Folder changed. Refresh to scan this CloudSave folder before saving."; };
        VersionLabel.Text = "Community preview · " + typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        var categories = Configuration.Schema["ui"]!["categories"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Categories.ItemsSource = categories;
        Categories.SelectedItem = "Players";
        Loaded += async (s, e) => { Refresh(false); if (payload == null) NewDocument("koth"); string? health = Arg("--update-health"); if (health != null && Path.GetFullPath(health).StartsWith(Path.GetFullPath(Path.Combine(dataRoot, "Updates")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Json.Atomic(health, "ready"); if (AutoUpdate.IsChecked == true && Arg("--no-update") == null) await CheckUpdate(false); };
        Closing += (s, e) => { if (e.Cancel) return; if ((dirty || invalid.Count > 0) && !Confirm("Discard the unsaved editor changes?")) { e.Cancel = true; return; } history?.Dispose(); };
    }
    string? Arg(string key)
    {
        int i = Array.IndexOf(args, key);
        return i >= 0 ? (i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : "") : null;
    }
    JsonObject Preferences()
    {
        try
        {
            return (JsonObject)Json.Read(File.ReadAllText(Path.Combine(dataRoot, "settings.json")));
        }
        catch { return new(); }
    }
    void SavePreferences()
    {
        var p = Preferences();
        p["cloudSave"] = Folder.Text;
        p["autoUpdate"] = AutoUpdate.IsChecked == true;
        p["layout"] = new JsonObject { ["modes"] = preferredModeWidth, ["categories"] = preferredCategoryWidth, ["mapDetails"] = preferredMapWidth };
        Json.Atomic(Path.Combine(dataRoot, "settings.json"), p.ToJsonString());
    }
    void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error) { Report(error); }
    }
    void Report(Exception error)
    {
        lastReportError = error;
        Status.Text = error.Message;
        try
        {
            Json.Atomic(Path.Combine(dataRoot, "Logs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".txt"), error.ToString());
        }
        catch { }
        MessageBox.Show(this, error.Message, "SGL", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    bool Confirm(string text) => MessageBox.Show(this, text, "SGL", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
    bool CanLeave() => !(dirty || invalid.Count > 0) || Confirm("Discard unsaved editor changes?");
    void Refresh(bool announce = true)
    {
        try
        {
            store = new GameStore(Folder.Text);
            var next = store.Read();
            history?.Dispose();
            history = null;
            history = new History(HistoryDirectory);
            string? commit = history.Capture(next, snapshot == null ? "Startup: capture saved gamemodes" : "Detected changes outside SGL");
            snapshot = next;
            FilterModes();
            RefreshMaps();
            RefreshSharingSources();
            UpdateRevisions();
            string backupRoot = Path.Combine(store.Root, "Backup");
            Backups.ItemsSource = Directory.Exists(backupRoot) ? Directory.GetDirectories(backupRoot).OrderDescending().ToArray() : [];
            SavePreferences();
            Status.Text = $"{next.Modes.Count} modes and {next.Maps.Count} maps found. " + (commit == null ? "History is up to date." : "Captured revision " + commit[..7] + ".");
        }
        catch (Exception error) { Status.Text = "Could not scan saves: " + error.Message; if (announce) Report(error); }
    }
    void RefreshClick(object s, RoutedEventArgs e) => Run(() => Refresh());
    void SearchChanged(object s, TextChangedEventArgs e)
    {
        if (Installed == null || snapshot == null)
            return;
        FilterModes();
    }
    void BrowseFolder(object s, RoutedEventArgs e)
    {
        if (!CanLeave())
            return;
        var dialog = new OpenFolderDialog { Title = "Choose Splitgate CloudSave folder" };
        if (dialog.ShowDialog(this) == true)
        {
            Folder.Text = dialog.FolderName;
            payload = null;
            selectedId = null;
            Refresh();
            NewDocument("koth");
        }
    }
    void ModeSelected(object s, SelectionChangedEventArgs e)
    {
        if (updatingModeSelection) return;
        UpdateModeSelectionButtons();
        if (SelectedModes is [SavedMode mode] && mode.Id != selectedId)
        {
            if (!CanLeave())
            {
                updatingModeSelection = true;
                try { Installed.SelectedItem = snapshot?.Modes.FirstOrDefault(m => m.Id == selectedId); }
                finally { updatingModeSelection = false; }
                UpdateModeSelectionButtons();
                return;
            }
            Load(mode.Payload, mode.Id);
        }
    }
    void Load(JsonObject p, string? id = null)
    {
        loading = true;
        payload = Configuration.Clone(p);
        opened = Configuration.Clone(p);
        selectedId = id;
        var rules = Configuration.Rules(payload);
        ruleContextId = rules[0].Id;
        RulePhase.ItemsSource = rules;
        RulePhase.SelectedValue = ruleContextId;
        if (id == null)
            editorShareId = Guid.NewGuid().ToString("N");
        openedArchiveHash = snapshot?.Modes.SingleOrDefault(m => m.Id == id)?.ArchiveHash;
        ModeName.Text = Configuration.Name(p);
        invalid.Clear();
        dirty = false;
        loading = false;
        ShowModeEditor();
        Render();
        RefreshSharingSources();
    }
    void NewDocument(string basis)
    {
        var p = Configuration.New(basis);
        p["Options"]!["CustomName"] = "New " + BaseModes.Find(basis).Name;
        Base.SelectedValue = basis;
        Load(p);
        Status.Text = "New mode. Choose a loadout and review the base rules.";
    }
    void NewMode(object s, RoutedEventArgs e)
    {
        if (CanLeave() && Base.SelectedItem is BaseMode basis)
            NewDocument(basis.Id);
    }
    void NameChanged(object s, TextChangedEventArgs e)
    {
        if (!loading && payload != null)
        {
            payload["Options"]!["CustomName"] = ModeName.Text;
            MarkDirty();
        }
    }
    void MarkDirty()
    {
        dirty = true;
        DirtyLabel.Text = "Unsaved changes";
    }
    void CategoryChanged(object s, SelectionChangedEventArgs e)
    {
        if (changingSettingsCategory) return;
        if (Categories.SelectedItem is string category) settingsCategoryBeforeSearch = category;
        if (SettingsSearch?.Text.Length > 0)
            SettingsSearch.Clear();
        if (Rows != null)
            Render();
        SettingsScroll?.ScrollToTop();
    }
    void SettingsSearchChanged(object s, TextChangedEventArgs e)
    {
        if (ClearSettingsSearchButton != null) ClearSettingsSearchButton.IsEnabled = !string.IsNullOrEmpty(SettingsSearch.Text);
        if (Categories != null)
        {
            changingSettingsCategory = true;
            try
            {
                if (!string.IsNullOrWhiteSpace(SettingsSearch.Text) && Categories.SelectedItem is string category)
                {
                    settingsCategoryBeforeSearch = category;
                    Categories.SelectedIndex = -1;
                }
                else if (string.IsNullOrWhiteSpace(SettingsSearch.Text) && Categories.SelectedIndex < 0)
                    Categories.SelectedItem = settingsCategoryBeforeSearch;
            }
            finally { changingSettingsCategory = false; }
        }
        Render();
        SettingsScroll?.ScrollToTop();
    }
    void ClearSettingsSearch(object s, RoutedEventArgs e) { SettingsSearch.Clear(); SettingsSearch.Focus(); }
    void SettingsSearchKeyDown(object s, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) { SettingsSearch.Clear(); e.Handled = true; }
    }
    static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var b) ? (b ? "True" : "False") : node is JsonValue text && text.TryGetValue<string>(out var str) ? str : node?.ToJsonString() ?? "";
    static Brush Brush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    void Render()
    {
        if (payload == null || Rows == null)
            return;
        Rows.Children.Clear();
        DirtyLabel.Text = invalid.Count > 0 ? "Correct invalid values before saving" : dirty ? "Unsaved changes" : "";
        if (SettingsSearchStatus != null) SettingsSearchStatus.Text = "";
        SaveButton.IsEnabled = Configuration.Supported(payload);
        if (!Configuration.Supported(payload))
        {
            Rows.Children.Add(new TextBlock { Text = "Unsupported base. This mode is preserved in history and can be exported, but cannot be edited yet.", TextWrapping = TextWrapping.Wrap });
            return;
        }
        string category = (string?)Categories.SelectedItem ?? "Players";
        string query = SettingsSearch?.Text.Trim() ?? "";
        if (query.Length > 0)
        {
            var matches = GamemodeUi.Fields(payload, CurrentRule).Where(f => SettingSearch.Matches(f, query)).ToArray();
            bool loadout = SettingSearch.LoadoutMatches(query);
            int count = matches.Length + (loadout ? 3 : 0);
            if (SettingsSearchStatus != null) SettingsSearchStatus.Text = $"{count} matching setting{(count == 1 ? "" : "s")} across all categories";
            foreach (string group in Configuration.Schema["ui"]!["categories"]!.AsArray().Select(n => n!.GetValue<string>()))
            {
                var fieldsInGroup = matches.Where(f => (string?)f["ui"]?["category"] == group).OrderBy(f => (int?)f["ui"]?["order"] ?? 0).ToArray();
                if (fieldsInGroup.Length == 0 && !(loadout && group == "Loadouts")) continue;
                Rows.Children.Add(new TextBlock { Text = group, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brush("#F3BE4F"), Margin = new Thickness(14, 14, 14, 8) });
                if (loadout && group == "Loadouts") RenderLoadout();
                foreach (var field in fieldsInGroup) RenderField(field);
            }
            if (count == 0) Rows.Children.Add(new TextBlock { Text = "No settings match this search.", Margin = new Thickness(14), TextWrapping = TextWrapping.Wrap });
            return;
        }
        if (category == "Loadouts")
        {
            RenderLoadout();
        }
        var fields = GamemodeUi.Fields(payload, CurrentRule).Where(f => (string?)f["ui"]?["category"] == category).OrderBy(f => (int?)f["ui"]?["order"] ?? 0).ToArray();
        if (fields.Length == 0)
            Rows.Children.Add(new TextBlock { Text = "No settings in this category for this base mode.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) });
        foreach (var f in fields)
            RenderField(f);
    }
    void RenderField(JsonObject f)
    {
        string id = (string)f["id"]!, label = (string?)f["ui"]?["label"] ?? (string)f["label"]!;
        var value = Configuration.Value(payload!, f, ruleContextId);
        bool boolean = (string)f["type"]! == "boolean";
        var panel = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };
        var border = new Border { Background = Brush("#1C1C3C"), BorderBrush = Brush("#494264"), BorderThickness = new Thickness(0, 0, 0, 1), Child = panel, Margin = new Thickness(0, 0, 0, 6) };
        var head = new DockPanel();
        var reset = new Button { Content = "Reset", FontSize = 12, Padding = new Thickness(8, 4, 8, 4) };
        reset.IsEnabled = value != null;
        DockPanel.SetDock(reset, Dock.Right);
        head.Children.Add(reset);
        head.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(head);
        var marker = new TextBlock { FontSize = 12, Foreground = Brush("#F3BE4F"), Margin = new Thickness(0, 5, 0, 6), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(marker);
        var help = new TextBlock { FontSize = 12, Foreground = Brush("#B9B7D6"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) };
        void Describe()
        {
            var v = Configuration.Value(payload!, f, ruleContextId);
            var notes = new List<string>();
            if (v == null)
                notes.Add("Not present in this saved base");
            if (!GamemodeUi.InGame(payload!, f, CurrentRule.Name))
                notes.Add("SGL extra · not native to " + ((string?)GamemodeUi.Profile(payload!)?["name"] ?? "this base mode") + " · " + CurrentRule.Name);
            if (ruleContextId != null && Configuration.IsShared(payload!, f, ruleContextId))
                notes.Add("Shared across rule phases");
            if (v != null && opened != null && !JsonNode.DeepEquals(v, Configuration.Value(opened, f, ruleContextId)))
                notes.Add("Changed from opened values");
            if (v != null && !boolean && double.TryParse(Text(v), CultureInfo.InvariantCulture, out var n) && f["ui"]?["min"] is JsonNode min && f["ui"]?["max"] is JsonNode max && (n < (double)min || n > (double)max))
                notes.Add("Outside in-game range · " + min + "–" + max);
            marker.Text = string.Join("  |  ", notes);
            help.Text = (string?)f["ui"]?["description"] ?? (string?)f["help"] ?? "";
            if (f["ui"]?["rangeNote"] is JsonNode rangeNote)
                help.Text += "  " + rangeNote.GetValue<string>();
            if (f["ui"]?["min"] != null)
                help.Text += $"  In-game: {f["ui"]!["min"]}–{f["ui"]!["max"]}. SGL: {f["min"]}–{f["max"]}.";
            if (opened != null)
                help.Text += "  Opened: " + Text(Configuration.Value(opened, f, ruleContextId)) + ".";
        }
        reset.Click += (s, e) => Run(() => { if (opened != null) { Configuration.Reset(payload!, opened, f, ruleContextId); invalid.Remove(id); MarkDirty(); Render(); } });
        if (boolean)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (bool choice in new[] { false, true })
            {
                bool blocked = choice && id == "weaponDrops" && Configuration.Value(payload!, Configuration.Fields.Single(n => (string)n["id"]! == "infiniteReserves"), ruleContextId)?.GetValue<bool>() == true;
                var button = new Button { Content = choice ? "ON" : "OFF", MinWidth = 85, IsEnabled = value != null && !blocked };
                AutomationProperties.SetName(button, label + (choice ? " On" : " Off"));
                if (value != null && bool.Parse(Text(value)) == choice)
                {
                    button.Background = Brush("#F3BE4F");
                    button.Foreground = Brush("#171027");
                }
                button.Click += (s, e) => Run(() => { Configuration.Set(payload!, f, choice.ToString(), ruleContextId); MarkDirty(); Render(); });
                row.Children.Add(button);
            }
            DockPanel.SetDock(row, Dock.Right);
            row.Width = 200;
            head.Children.Insert(head.Children.Count - 1, row);
        }
        else if ((string)f["type"]! == "enum")
        {
            var choices = f["choices"]!.AsArray().Select(n => new Item((string)n!["value"]!, (string)n["label"]!)).ToList();
            if (value != null && !choices.Any(c => c.Id == Text(value)))
                choices.Insert(0, new Item(Text(value), "Current: " + Text(value)));
            var pick = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = Text(value), IsEnabled = value != null, Width = 240 };
            AutomationProperties.SetName(pick, label);
            AutomationProperties.SetAutomationId(pick, id);
            pick.SelectionChanged += (s, e) => Run(() => { if (pick.SelectedItem is Item item) { Configuration.Set(payload!, f, item.Id, ruleContextId); MarkDirty(); Describe(); } });
            DockPanel.SetDock(pick, Dock.Right);
            head.Children.Insert(head.Children.Count - 1, pick);
        }
        else
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            double lower = (double)f["min"]!, upper = (double)f["max"]!;
            if (f["ui"]?["min"] != null)
            {
                lower = Math.Max(lower, (double)f["ui"]!["min"]!);
                upper = Math.Min(upper, (double)f["ui"]!["max"]!);
            }
            var slider = new Slider { Minimum = lower, Maximum = upper, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center, IsEnabled = value != null, SmallChange = (double)f["step"]!, IsSnapToTickEnabled = true, TickFrequency = (double)f["step"]! };
            var input = new TextBox { Text = invalid.GetValueOrDefault(id) ?? Text(value), IsEnabled = value != null };
            if (invalid.ContainsKey(id)) input.BorderBrush = Brush("#FF9696");
            AutomationProperties.SetName(input, label);
            AutomationProperties.SetAutomationId(input, id);
            AutomationProperties.SetName(slider, label + " slider");
            AutomationProperties.SetAutomationId(slider, id + "Slider");
            Grid.SetColumn(input, 1);
            double.TryParse(Text(value), CultureInfo.InvariantCulture, out double initial);
            slider.Value = Math.Clamp(initial, lower, upper);
            bool syncing = false;
            slider.ValueChanged += (s, e) => { if (!syncing) input.Text = e.NewValue.ToString(CultureInfo.InvariantCulture); };
            input.TextChanged += (s, e) => { if (loading) return; try { Configuration.Set(payload!, f, input.Text, ruleContextId); invalid.Remove(id); input.BorderBrush = Brush("#69618E"); syncing = true; slider.Value = Math.Clamp(double.Parse(input.Text, CultureInfo.InvariantCulture), lower, upper); syncing = false; MarkDirty(); Describe(); } catch { invalid[id] = input.Text; input.BorderBrush = Brush("#FF9696"); DirtyLabel.Text = "Correct invalid values before saving"; } };
            row.Children.Add(slider);
            row.Children.Add(input);
            DockPanel.SetDock(row, Dock.Right);
            row.SetBinding(WidthProperty, new System.Windows.Data.Binding("ActualWidth") { Source = SettingsScroll, Converter = new ControlWidth(200, 320, 240) });
            row.Margin = new Thickness(10, 0, 10, 0);
            head.Children.Insert(head.Children.Count - 1, row);
        }
        panel.Children.Add(help);
        Describe();
        Rows.Children.Add(border);
    }
    void RenderLoadout()
    {
        Rows.Children.Add(loadoutTools);
        Rows.Children.Add(new TextBlock { Text = "Custom starting loadout · SGL extra", Foreground = Brush("#F3BE4F"), Margin = new Thickness(0, 0, 0, 14) });
        var ids = Configuration.Loadout(payload!, ruleContextId);
        Rows.Children.Add(new TextBlock { Text = ids == null ? "This mode uses base loadouts. Choosing all three items will set a custom starting loadout." : "Explicit loadout edits replace starting-item selection. Other rules remain unchanged.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        ComboBox[] picks = new ComboBox[3];
        for (int i = 0; i < 3; i++)
        {
            Rows.Children.Add(new TextBlock { Text = new[] { "Primary weapon", "Secondary weapon", "Equipment" }[i], Margin = new Thickness(0, 12, 0, 4) });
            int slot = i;
            var choices = Configuration.Catalog[i == 2 ? "equipment" : "weapons"]!.AsArray().Select(n => new Item((string)n!["id"]!, (string)n["name"]!)).ToArray();
            var pick = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = ids?[i] };
            picks[i] = pick;
            pick.SelectionChanged += (s, e) => { if (picks.All(p => p?.SelectedItem is Item)) { Configuration.SetLoadout(payload!, picks.Select(p => ((Item)p.SelectedItem).Id).ToArray(), ruleContextId); MarkDirty(); } };
            Rows.Children.Add(pick);
        }
    }
    sealed record Item(string Id, string Name);
    void Randomize(object s, RoutedEventArgs e) => Run(() => { if (payload == null) return; var ids = Configuration.Loadout(payload, ruleContextId) ?? ["", "", ""]; bool[] locks = [LockPrimary.IsChecked == true, LockSecondary.IsChecked == true, LockEquipment.IsChecked == true]; for (int i = 0; i < 3; i++) if (!locks[i]) { var pool = Configuration.Catalog[i == 2 ? "equipment" : "weapons"]!.AsArray().Select(n => (string)n!["id"]!).Where(id => i == 2 || id != ids[1 - i]).ToArray(); ids[i] = pool[Random.Shared.Next(pool.Length)]; } Configuration.SetLoadout(payload, ids, ruleContextId); MarkDirty(); SettingsSearch.Clear(); Categories.SelectedItem = "Loadouts"; Render(); });
    void ResetOpened(object s, RoutedEventArgs e)
    {
        if (opened != null && Confirm("Reset all editor changes to the values when this mode was opened?"))
        {
            string? expected = openedArchiveHash;
            Load(opened, selectedId);
            openedArchiveHash = expected;
        }
    }
    void EnsureValid()
    {
        if (payload == null)
            throw new InvalidDataException("Open a mode first.");
        if (invalid.Count > 0)
            throw new InvalidDataException("Correct the highlighted invalid numeric values first.");
        Modes.Validate(Configuration.Wrapped(payload));
    }
    void SaveToGame(object s, RoutedEventArgs e) => Run(() =>
    {
        EnsureValid();
        if (store == null || snapshot == null)
            throw new InvalidDataException("Locate your CloudSave folder and refresh first.");
        if (selectedId != null && snapshot.Modes.SingleOrDefault(m => m.Id == selectedId)?.ArchiveHash != openedArchiveHash)
            throw new IOException("This mode changed outside SGL after you opened it. Reopen it, or duplicate your edited mode before saving.");
        if (Modes.GameRunning())
            throw new InvalidOperationException("Close Splitgate before saving to the game. You can keep editing while it runs.");
        string outside = string.Join(", ", Configuration.Rules(payload!).SelectMany(rule => GamemodeUi.Fields(payload!, rule)
            .Where(f => f["ui"]?["min"] != null && Configuration.Value(payload!, f, rule.Id) is JsonNode v && double.TryParse(Text(v), CultureInfo.InvariantCulture, out var n) && (n < (double)f["ui"]!["min"]! || n > (double)f["ui"]!["max"]!))
            .Select(f => rule.Name + ": " + (string)f["ui"]!["label"]!)));
        if (outside.Length > 0 && !Confirm("Outside documented in-game slider range: " + outside + ". The game may clamp or ignore these values. Save anyway?"))
            return;
        if (history == null)
            throw new IOException("History is unavailable. Refresh and resolve the history error before saving.");
        history.Capture(snapshot, "Checkpoint before SGL save");
        lastBackup = store.Apply(snapshot, selectedId, payload!);
        var next = store.Read();
        history.Capture(next, "SGL save: " + Configuration.Name(payload!));
        snapshot = next;
        dirty = false;
        Refresh(false);
        var saved = next.Modes.Single(m => m.Name == Configuration.Name(payload!));
        Load(saved.Payload, saved.Id);
        Status.Text = "Saved to game. Backup: " + lastBackup;
    });
    void Duplicate(object s, RoutedEventArgs e)
    {
        if (payload != null)
        {
            selectedId = null;
            ModeName.Text = Configuration.Name(payload) + " Copy";
            MarkDirty();
        }
    }
    void Import(object s, RoutedEventArgs e) => Run(() => { if (!CanLeave()) return; var dialog = new OpenFileDialog { Filter = "Mode recipes / packs|*.json" }; if (dialog.ShowDialog(this) != true) return; var recipes = Modes.ReadRecipes(dialog.FileName); var choices = recipes.Select(r => new Choice((string)r["name"], () => Load(Configuration.ReadArchive(Modes.Pack(r))))).ToList(); if (recipes.Count > 1) choices.Insert(0, new Choice($"Install all {recipes.Count} modes (skip existing names)", () => Run(() => InstallPack(recipes)))); Choose("Double-click a mode or install the complete pack", choices.ToArray()); });
    void InstallPack(List<Dictionary<string, object>> recipes)
    {
        if (store == null || history == null)
            throw new IOException("Refresh your game saves before installing a pack.");
        if (!Confirm($"Install {recipes.Count} modes? Existing names will be skipped and the save index backed up. Splitgate must be closed."))
            return;
        var current = store.Read();
        history.Capture(current, "Checkpoint before pack import");
        var result = Modes.InstallModes(recipes, store.Root, Modes.GameRunning, Modes.ExistingModeAction.Skip);
        history.Capture(store.Read(), "Imported mode pack");
        dirty = false;
        Refresh();
        Status.Text = result.Report;
    }
    sealed record Choice(string Name, Action Apply);
    void Choose(string title, Choice[] choices)
    {
        var dialog = new Window { Owner = this, Title = title, Width = 600, Height = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = new ListBox { ItemsSource = choices, DisplayMemberPath = "Name", Margin = new Thickness(16) };
        list.MouseDoubleClick += (s, e) => { if (list.SelectedItem is Choice c) { dialog.Close(); c.Apply(); } };
        dialog.Content = list;
        dialog.ShowDialog();
    }
    void Presets(object s, RoutedEventArgs e) => Run(() => { if (!CanLeave()) return; string[] names = ["Sticky Fists TDM", "Sticky Fists KOTH", "Sticky Fists Domination", "Ranked Loadout 1v1", "Tempo 1v1", "Blink 1v1"]; Choose("Double-click a community preset", names.Select(name => new Choice(name, () => Load((JsonObject)Json.Read(Modes.Resource(name.Replace(' ', '-') + ".json"))))).ToArray()); });
    void Export(object s, RoutedEventArgs e) => Run(() => { if (payload == null) throw new InvalidDataException("Open a mode first."); if (Configuration.Supported(payload)) EnsureValid(); var dialog = new SaveFileDialog { Filter = "Mode JSON|*.json", FileName = "Mode.json" }; if (dialog.ShowDialog(this) == true) Json.Atomic(dialog.FileName, Json.Canonical(payload)); });
    void ExportPack(object s, RoutedEventArgs e) => Run(() => { if (snapshot == null) throw new IOException("Refresh first."); var dialog = new SaveFileDialog { Filter = "Mode pack JSON|*.json", FileName = "SGL-Modes.json" }; if (dialog.ShowDialog(this) == true) { var modes = new JsonArray(snapshot.Modes.Select(m => (JsonNode)new JsonObject { ["name"] = m.Name, ["payload"] = m.Payload.DeepClone() }).ToArray()); Json.Atomic(dialog.FileName, new JsonObject { ["format"] = "spingate-payload-pack", ["version"] = 1, ["modes"] = modes }.ToJsonString()); } });
    void UpdateRevisions()
    {
        var revisions = history?.Revisions() ?? [];
        BeforeRevision.ItemsSource = revisions;
        AfterRevision.ItemsSource = revisions;
        if (revisions.Length > 0)
        {
            AfterRevision.SelectedIndex = 0;
            BeforeRevision.SelectedIndex = Math.Min(1, revisions.Length - 1);
        }
    }
    void ShowModeHistory(object sender, RoutedEventArgs e)
    {
        ModeEditor.Visibility = Visibility.Collapsed;
        ModeHistoryPanel.Visibility = Visibility.Visible;
        UpdateRevisions();
    }
    void ShowModeEditor()
    {
        ModeHistoryPanel.Visibility = Visibility.Collapsed;
        ModeEditor.Visibility = Visibility.Visible;
    }
    void BackToModeEditor(object sender, RoutedEventArgs e) => ShowModeEditor();
    void RevisionChanged(object s, SelectionChangedEventArgs e)
    {
        if (history != null && BeforeRevision.SelectedItem is Revision a && AfterRevision.SelectedItem is Revision b)
            Run(() => DiffText.Text = history.Compare(a.Id, b.Id));
    }
    void RestoreRevision(object s, RoutedEventArgs e) => Run(() => { if (history == null || BeforeRevision.SelectedItem is not Revision r) throw new InvalidDataException("Select an older snapshot first."); if (!CanLeave()) return; var old = history.Snapshot(r.Id); void Restore(string id) { Load(history.Mode(r.Id, id), snapshot?.Modes.Any(m => m.Id == id) == true ? id : null); MarkDirty(); NavigateToContent("mode", "installed"); Status.Text = "Revision loaded into editor. Review and click Save to game to restore it as a new revision."; } if (selectedId != null && old[selectedId] != null) Restore(selectedId); else Choose("Double-click a historical mode to restore", old.Select(p => new Choice(Configuration.Name(p.Value!.AsObject()), () => Restore(p.Key))).ToArray()); });
    void OpenBackupFolder(object s, RoutedEventArgs e) => Run(() => OpenPath(Backups.SelectedItem as string ?? lastBackup ?? Path.Combine(Folder.Text, "Backup")));
    void RestoreBackup(object s, RoutedEventArgs e) => Run(() => { if (store == null || snapshot == null || Backups.SelectedItem is not string path) throw new InvalidDataException("Select a backup first."); if (!CanLeave() || !Confirm("Restore the COMPLETE save index from this backup? Later registrations will no longer be listed. A safety backup is created first.\n\n" + path)) return; history!.Capture(snapshot, "Checkpoint before manifest restore"); lastBackup = store.RestoreBackup(snapshot, path); history.Capture(store.Read(), "Restored complete manifest backup"); dirty = false; Refresh(); });
    static void OpenPath(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    void OpenData(object s, RoutedEventArgs e) => Run(() => OpenPath(dataRoot));
    void OpenGuide(object s, RoutedEventArgs e) => Run(() => { string local = Path.Combine(AppContext.BaseDirectory, "Guide", "index.html"); OpenPath(File.Exists(local) ? local : "https://sglifers.github.io/SGL/"); });
    void UpdatePreference(object s, RoutedEventArgs e)
    {
        if (!loading)
            SavePreferences();
    }
    async void CheckUpdates(object s, RoutedEventArgs e) => await CheckUpdate(true);
    async Task CheckUpdate(bool manual)
    {
        try
        {
            Status.Text = "Checking official releases…";
            var version = typeof(MainWindow).Assembly.GetName().Version!;
            var update = await Updates.Download(version, Path.Combine(dataRoot, "Updates", Guid.NewGuid().ToString("N")), Updater.PublicKey);
            if (update == null)
            {
                Status.Text = "SGL is up to date.";
                return;
            }
            Status.Text = "Verified update " + update.Value.Release.Version + " downloaded.";
            if (dirty || invalid.Count > 0 || libraryBusy || modsBusy || modConfigDirty || hostingAnnouncementRequest != null)
            {
                Status.Text += " Save your changes and restart SGL to apply it.";
                return;
            }
            if (manual && !Confirm("Install the verified update and restart SGL?"))
                return;
            Updater.Begin(update.Value.Package, dataRoot);
            dirty = false;
            Application.Current.Shutdown();
        }
        catch (Exception error) { Status.Text = "Update unavailable; continuing with the installed version. " + error.Message; }
    }
}
