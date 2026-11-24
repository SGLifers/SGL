using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using SGL.Core;
namespace SGL.Desktop;

public partial class MainWindow
{
    sealed class BrowseContext
    {
        public LibraryItem[] Items = [];
        public string? Cursor;
        public string Tag = "", Creator = "", Search = "", Source = "all";
        public bool Loaded;
    }
    readonly Dictionary<string, BrowseContext> browseContexts = [];
    string browseKind = "mode", browseView = "community";
    bool navigationReady, changingContentView;
    string BrowseKey => browseKind + ":" + browseView;
    bool BrowsingMine => browseView == "mine";
    bool RemoteViewSelected => (MapsTab.IsSelected || ModesTab.IsSelected) && (MapsTab.IsSelected ? MapViews : ModeViews).SelectedIndex > 0;
    string? SignedInUser => (string?)Session(LibraryRoot).Load()?["user"]?["id"];
    LibraryBrowseEntry[] SelectedLibraryRows => LibraryItems?.SelectedItems.OfType<LibraryBrowseEntry>().ToArray() ?? [];
    LibraryItem? SelectedLibraryItem => SelectedLibraryRows is [var row] ? row.Publication : null;
    LibraryItem? publishingParent;
    LibraryRevision? publishingRevision;
    JsonObject? OwnPublicationRecord(LibraryItem item) => item.OwnerId == SignedInUser
        ? SharedState.PublicationForItem(LibraryRoot.TrimEnd('/') + "/#" + SignedInUser, item.Id) : null;

    void InitializeContentNavigation()
    {
        navigationReady = true;
        Tabs.SelectionChanged += ContentViewChanged;
    }
    async void ContentViewChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!navigationReady || changingContentView || (e.Source != Tabs && e.Source != ModeViews && e.Source != MapViews)) return;
        if (SettingsTab.IsSelected || ModsTab.IsSelected) return;
        var views = MapsTab.IsSelected ? MapViews : ModeViews;
        if (views.SelectedIndex == 0) { if (MapsTab.IsSelected) { UpdateInstalledMapDetails(); } return; }
        ActivateLibraryContext(MapsTab.IsSelected ? "map" : "mode", views.SelectedIndex == 2 ? "mine" : "community");
        await EnsureContentLoaded();
    }
    void SaveBrowseContext()
    {
        var context = browseContexts.GetValueOrDefault(BrowseKey) ?? new BrowseContext();
        context.Items = sharedItems.ToArray(); context.Cursor = libraryCursor;
        context.Tag = appliedLibraryTag; context.Creator = appliedLibraryCreator;
        context.Search = LibrarySearch.Text; context.Source = (string?)((ComboBoxItem?)LibrarySource.SelectedItem)?.Tag ?? "all";
        browseContexts[BrowseKey] = context;
    }
    void ActivateLibraryContext(string kind, string view)
    {
        bool different = kind != browseKind || view != browseView;
        if (different) SaveBrowseContext();
        browseKind = kind; browseView = view;
        var host = kind == "map" ? view == "mine" ? MapUploadsHost : MapCommunityHost : view == "mine" ? ModeUploadsHost : ModeCommunityHost;
        if (LibraryWorkspace.Parent is ContentControl previous && previous != host) previous.Content = null;
        host.Content = LibraryWorkspace;
        LibrarySource.Visibility = kind == "map" && view == "community" ? Visibility.Visible : Visibility.Collapsed;
        RefreshCatalogButton.Visibility = kind == "map" && view == "community" ? Visibility.Visible : Visibility.Collapsed;
        if (different)
        {
            var context = browseContexts.GetValueOrDefault(BrowseKey) ?? new BrowseContext();
            filteringSharedItems = true;
            try
            {
                sharedItems.Clear(); sharedItems.AddRange(context.Items); libraryCursor = context.Cursor;
                LibraryItems.UnselectAll();
                appliedLibraryTag = context.Tag; appliedLibraryCreator = context.Creator;
                LibraryTag.Text = context.Tag; LibraryCreator.Text = context.Creator; LibrarySearch.Text = context.Search;
                LibrarySource.SelectedItem = LibrarySource.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == context.Source);
                LibraryRevisions.ItemsSource = null; LibraryCompareRevision.ItemsSource = null;
            }
            finally { filteringSharedItems = false; }
            PublicationEditor.Visibility = Visibility.Collapsed;
            LibraryBrowserPanel.Visibility = Visibility.Visible;
        }
        FilterSharedItems();
        RefreshSharingSources();
        LibraryStatus.Text = view == "mine" && SignedInUser == null ? "Sign in with Discord to see your uploads. Local editing and portable exports work offline."
            : view == "mine" ? "Your public, unlisted and private uploads." : "Browse community " + (kind == "map" ? "maps and map + gamemode bundles." : "gamemodes and map + gamemode bundles.");
    }
    async Task EnsureContentLoaded()
    {
        string key = BrowseKey;
        if (browseKind == "map" && !BrowsingMine && !catalogLoaded && !catalogLoading) await LoadCatalog(false);
        if (key != BrowseKey || libraryBusy || (BrowsingMine && SignedInUser == null)) return;
        if (browseContexts.GetValueOrDefault(key)?.Loaded != true) await RefreshSharedLibrary();
    }
    void NavigateToContent(string kind, string view)
    {
        changingContentView = true;
        try
        {
            var section = kind == "map" ? MapsTab : ModesTab;
            var views = kind == "map" ? MapViews : ModeViews;
            int index = view == "installed" ? 0 : view == "mine" ? 2 : 1;
            section.IsEnabled = true; ((TabItem)views.Items[index]).IsEnabled = true;
            section.IsSelected = true; views.SelectedIndex = index;
        }
        finally { changingContentView = false; }
        if (view != "installed") ActivateLibraryContext(kind, view);
        UpdateNavigationBusy();
    }
    void UpdateNavigationBusy()
    {
        bool busy = libraryBusy || mapDownload != null || modsBusy;
        LibraryCancelButton.IsEnabled = busy;
        LibraryItems.IsEnabled = !busy;
        UpdateLibrarySelectionState();
        foreach (var tab in Tabs.Items.Cast<TabItem>()) tab.IsEnabled = !busy || tab.IsSelected;
        foreach (var views in new[] { ModeViews, MapViews })
            foreach (var tab in views.Items.Cast<TabItem>()) tab.IsEnabled = !busy || tab.IsSelected;
    }
    void ShowPublicationForm()
    {
        ((ComboBoxItem)PublishKind.Items[0]).Visibility = browseKind == "map" ? Visibility.Visible : Visibility.Collapsed;
        ((ComboBoxItem)PublishKind.Items[1]).Visibility = browseKind == "mode" ? Visibility.Visible : Visibility.Collapsed;
        LibraryBrowserPanel.Visibility = Visibility.Collapsed;
        PublicationEditor.Visibility = Visibility.Visible;
        RefreshSharingSources();
    }
    void ShowNewPublication(object sender, RoutedEventArgs e)
    {
        string kind = browseKind;
        NavigateToContent(kind, "mine");
        ResetPublicationForm(kind);
        ShowPublicationForm();
    }
    void ResetPublicationForm(string kind)
    {
        publishingParent = null; publishingRevision = null;
        PublishDescription.Clear(); PublishCredits.Clear(); PublishTitle.Clear();
        PublishNote.Text = "Initial publication";
        PublishVisibility.SelectedIndex = 0; PublishAction.SelectedIndex = 0;
        PublishKind.SelectedIndex = kind == "map" ? 0 : 1;
    }
    async void BackToUploads(object sender, RoutedEventArgs e)
    {
        PublicationEditor.Visibility = Visibility.Collapsed;
        LibraryBrowserPanel.Visibility = Visibility.Visible;
        await EnsureContentLoaded();
    }
    void ShareInstalledMap(object sender, RoutedEventArgs e) => Run(() =>
    {
        var map = SelectedInstalledMap ?? throw new InvalidOperationException("Select one installed map to share.");
        NavigateToContent("map", "mine");
        ResetPublicationForm("map");
        RefreshSharingSources(); PublishMap.SelectedItem = snapshot!.Maps.First(m => m.Id == map.Id);
        ShowPublicationForm();
    });
    void PreparePublicationUpdate(object sender, RoutedEventArgs e) => PreparePublication(false);
    void PreparePublicationFork(object sender, RoutedEventArgs e) => PreparePublication(true);
    void PreparePublication(bool fork) => Run(() =>
    {
        var selected = SelectedShared();
        if (!fork && selected.Item.OwnerId != SignedInUser) throw new InvalidOperationException("Only your own uploads can be updated.");
        publishingParent = selected.Item; publishingRevision = selected.Revision;
        NavigateToContent(selected.Item.Kind == "mode" ? "mode" : "map", "mine");
        PublishKind.SelectedIndex = selected.Item.Kind == "map" ? 0 : selected.Item.Kind == "mode" ? 1 : 2;
        PublishAction.SelectedIndex = fork ? 2 : 1;
        PublishTitle.Text = selected.Item.Title; PublishDescription.Text = selected.Item.Description;
        PublishCredits.Text = (string?)selected.Item.Json["credits"] ?? "";
        var visibility = (string?)selected.Item.Json["visibility"];
        PublishVisibility.SelectedIndex = visibility == "public" ? 1 : 0;
        PublishNote.Text = fork ? "My version" : "Updated configuration";
        ShowPublicationForm();
        var tracked = SharedState.Installed(LibraryRoot, selected.Item.Id) ?? OwnPublicationRecord(selected.Item);
        PublishMode.SelectedValue = selected.Item.Kind == "mode" ? (string?)tracked?["localFileId"] : null;
        PublishMap.SelectedItem = selected.Item.Kind == "map" ? snapshot?.Maps.FirstOrDefault(m => m.Id == (string?)tracked?["localFileId"]) : null;
        LibraryStatus.Text = "Choose the local content to publish. The original creator credits are retained.";
    });
    void OpenInstalledPublication(object sender, RoutedEventArgs e) => Run(() =>
    {
        var item = SelectedLibraryItem ?? throw new InvalidOperationException("Select one publication first.");
        var record = SharedState.Installed(LibraryRoot, item.Id) ?? OwnPublicationRecord(item);
        string? id = (string?)record?["localFileId"];
        if (item.Kind == "mode" && snapshot?.Modes.FirstOrDefault(m => m.Id == id) is SavedMode mode && CanLeave())
        { NavigateToContent("mode", "installed"); Load(mode.Payload, mode.Id); }
        else if (item.Kind == "map" && snapshot?.Maps.Any(m => m.Id == id) == true)
        { NavigateToContent("map", "installed"); SelectInstalledMap(id); }
        else throw new InvalidOperationException("Install this content first. Bundles can be opened from their installed map and gamemode entries.");
    });
}
