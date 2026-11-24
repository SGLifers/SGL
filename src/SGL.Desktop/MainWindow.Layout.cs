using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
namespace SGL.Desktop;

public partial class MainWindow
{
    double preferredModeWidth = 320, preferredCategoryWidth = 135, preferredMapWidth = 380;
    bool resizingPanes;
    void RestorePaneWidths(JsonObject prefs)
    {
        double Read(string key, double fallback) => double.TryParse(prefs["layout"]?[key]?.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double width) && double.IsFinite(width) ? width : fallback;
        preferredModeWidth = Math.Clamp(Read("modes", 320), 220, 650);
        preferredCategoryWidth = Math.Clamp(Read("categories", 135), 105, 260);
        preferredMapWidth = Math.Clamp(Read("mapDetails", 380), 280, 750);
        ApplyPaneWidths();
    }
    void ApplyPaneWidths()
    {
        if (resizingPanes)
            return;
        resizingPanes = true;
        try
        {
            void Size(Grid grid, ColumnDefinition column, double preferred, double otherMinimum, double available)
            {
                // Grid's fixed/minimum columns can make ActualWidth overflow its slot.
                // Constrain against the workspace, so a narrower window shrinks the panes.
                double width = available > 0 ? Math.Min(grid.ActualWidth > 0 ? grid.ActualWidth : available, available) : grid.ActualWidth;
                double upper = width > 0 ? Math.Max(column.MinWidth, Math.Min(column.MaxWidth, width - otherMinimum - 10)) : column.MaxWidth;
                column.Width = new GridLength(Math.Clamp(preferred, column.MinWidth, upper));
            }
            Size(ModePanes, ModeSidebar, preferredModeWidth, 560, ModeViews.ActualWidth - ModePanes.Margin.Left - ModePanes.Margin.Right);
            Size(SettingsPanes, CategorySidebar, preferredCategoryWidth, 420, ModeEditor.ActualWidth);
            Size(MapPanes, MapDetailsPane, preferredMapWidth, 300, MapViews.ActualWidth - 32);
        }
        finally { resizingPanes = false; }
    }
    void RememberPaneWidths()
    {
        preferredModeWidth = ModeSidebar.Width.Value;
        preferredCategoryWidth = CategorySidebar.Width.Value;
        preferredMapWidth = MapDetailsPane.Width.Value;
        ApplyPaneWidths();
        SavePreferences();
    }
    void PaneResizeCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!e.Canceled)
            Dispatcher.BeginInvoke(() => Run(RememberPaneWidths));
    }
    void PaneResizeKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right)
            Run(RememberPaneWidths);
    }
    void ResetPaneWidths(object sender, RoutedEventArgs e) => Run(() =>
    {
        preferredModeWidth = 320;
        preferredCategoryWidth = 135;
        preferredMapWidth = 380;
        ApplyPaneWidths();
        SavePreferences();
    });
    sealed class ControlWidth(double minimum, double maximum, double reserved) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is double width ? Math.Clamp(width - reserved, minimum, maximum) : maximum;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
