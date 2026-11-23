using System.Windows;
using System.Windows.Controls;
using SGL.Core;

namespace SGL.Desktop;

public partial class MainWindow
{
    sealed record MapListItem(string Id, string Name, string Author, string Category, string TagSummary, CommunityMap? Community, SavedMap? Installed);
    sealed record MapTagOption(string? Tag, string Name);
    Dictionary<string, string[]> mapTags = new(StringComparer.Ordinal);
    bool updatingMapTagFilter;
    MapTags TagStore => new(Path.Combine(Path.GetDirectoryName(HistoryDirectory)!, "map-tags.json"));
    SavedMap? SelectedInstalledMap => (InstalledMaps?.SelectedItem as MapListItem)?.Installed;
    CommunityMap? SelectedCommunityMap => (CommunityMaps?.SelectedItem as MapListItem)?.Community;
    string[] TagsFor(string id) => mapTags.GetValueOrDefault(id) ?? [];
    string TagSummary(string id) => string.Join(" · ", TagsFor(id));
    MapListItem MapRow(CommunityMap map) => new(map.Id, map.Name, map.Author, map.Category, TagSummary(map.Id), map, null);
    MapListItem MapRow(SavedMap map) => new(map.Id, map.Name, map.Author, "", TagSummary(map.Id), null, map);
    string? SelectedTag => (MapTagFilter?.SelectedItem as MapTagOption)?.Tag;
    void SelectInstalledMap(string? id) => InstalledMaps.SelectedItem = InstalledMaps.Items.OfType<MapListItem>().FirstOrDefault(m => m.Id == id);
    void RefreshMapTagFilter()
    {
        if (MapTagFilter == null) return;
        string? current = SelectedTag;
        var knownIds = mapCatalog.Select(m => m.Id).Concat(snapshot?.Maps.Select(m => m.Id) ?? []).ToHashSet(StringComparer.Ordinal);
        var tags = mapTags.Where(p => knownIds.Contains(p.Key)).SelectMany(p => p.Value).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        var options = new[] { new MapTagOption(null, "All tags") }.Concat(tags.Select(t => new MapTagOption(t, t))).ToArray();
        updatingMapTagFilter = true;
        try
        {
            MapTagFilter.ItemsSource = options;
            MapTagFilter.SelectedItem = options.FirstOrDefault(t => string.Equals(t.Tag, current, StringComparison.OrdinalIgnoreCase)) ?? options[0];
        }
        finally { updatingMapTagFilter = false; }
    }
    void MapTagFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingMapTagFilter) return;
        FilterMaps();
        FilterInstalledMaps();
    }
    void InstalledMapSearchChanged(object sender, TextChangedEventArgs e) => FilterInstalledMaps();
    void FilterInstalledMaps()
    {
        if (InstalledMaps == null || InstalledMapSearch == null) return;
        string? selected = SelectedInstalledMap?.Id;
        string search = InstalledMapSearch.Text.Trim();
        InstalledMaps.ItemsSource = snapshot?.Maps.Where(m => MapTags.Matches(TagsFor(m.Id), "", SelectedTag)
            && (search.Length == 0 || m.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || m.Author.Contains(search, StringComparison.OrdinalIgnoreCase) || MapTags.Matches(TagsFor(m.Id), search)))
            .OrderBy(m => m.Name).Select(MapRow).ToArray() ?? [];
        SelectInstalledMap(selected);
        UpdateMapTagDetails();
    }
    (string Id, string Name)? TagTarget => snapshot == null ? null : SelectedInstalledMap is SavedMap installed ? (installed.Id, installed.Name)
        : SelectedCommunityMap is CommunityMap community ? (community.Id, community.Name) : null;
    void UpdateMapTagDetails()
    {
        if (MapTagTitle == null) return;
        var target = TagTarget;
        MapTagTitle.Text = target is { } map ? "Local tags · " + map.Name : "Local tags";
        MapTagDetails.Text = target is { } selected ? TagSummary(selected.Id) is { Length: > 0 } tags ? tags : "No tags yet." : "Select a map to edit its tags.";
        EditMapTagsButton.IsEnabled = target != null;
    }
    void EditMapTags(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (TagTarget is not { } target) return;
        // Capture the profile before opening the editor, so a later refresh cannot
        // apply this edit to a different save folder.
        var tagStore = TagStore;
        var dialog = new Window { Owner = this, Title = "SGL · Map tags", Width = 560, Height = 330, MinWidth = 420, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new Grid { Margin = new Thickness(24) };
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new TextBlock { Text = "Tags for " + target.Name, TextWrapping = TextWrapping.Wrap, FontSize = 18, Margin = new Thickness(0, 0, 0, 12) };
        var input = new TextBox { Text = string.Join(", ", TagsFor(target.Id)), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = 2000 };
        var details = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var help = new TextBlock { Text = "Separate tags with commas. Examples: TDM, 1v1, Friday event. Up to 20 tags, 40 characters each. Tags are included when you publish or export this map.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#B9B7D6"), FontSize = 12 };
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var save = new Button { Content = "Save tags", IsDefault = true, Background = Brush("#F3BE4F"), Foreground = Brush("#171027") };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        save.Click += (_, _) =>
        {
            try
            {
                tagStore.Set(target.Id, MapTags.Parse(input.Text));
                dialog.DialogResult = true;
            }
            catch (Exception error) { help.Text = error.Message; }
        };
        buttons.Children.Add(save); buttons.Children.Add(cancel);
        details.Children.Add(help); details.Children.Add(buttons);
        Grid.SetRow(input, 1); Grid.SetRow(details, 2);
        panel.Children.Add(heading); panel.Children.Add(input); panel.Children.Add(details);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        if (dialog.ShowDialog() != true) return;
        mapTags = TagStore.Read();
        RefreshMapTagFilter(); FilterMaps(); FilterInstalledMaps(); UpdateMapTagDetails();
        UpdatePublishMapTags();
        MapStatus.Text = "Saved local tags for " + target.Name + ".";
    });
}
