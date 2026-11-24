using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
namespace SGL.Desktop;

public static class DropDownSearch
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(DropDownSearch), new PropertyMetadata(false, EnabledChanged));
    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query", typeof(string), typeof(DropDownSearch), new PropertyMetadata("", QueryChanged));
    static readonly ConditionalWeakTable<ComboBox, SearchState> States = new();
    sealed class SearchState
    {
        public ListCollectionView? View;
        public object? Committed;
        public bool Filtering;
    }
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static string GetQuery(DependencyObject target) => (string)target.GetValue(QueryProperty);
    public static void SetQuery(DependencyObject target, string value) => target.SetValue(QueryProperty, value);
    static void EnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not ComboBox combo) return;
        if ((bool)e.NewValue)
        {
            combo.SetCurrentValue(Selector.IsSynchronizedWithCurrentItemProperty, false);
            combo.DropDownOpened += Opened;
            combo.DropDownClosed += Closed;
            combo.SelectionChanged += Selected;
        }
        else
        {
            Closed(combo, EventArgs.Empty);
            combo.DropDownOpened -= Opened;
            combo.DropDownClosed -= Closed;
            combo.SelectionChanged -= Selected;
        }
    }
    static void Opened(object? sender, EventArgs e)
    {
        var combo = (ComboBox)sender!;
        var state = States.GetOrCreateValue(combo);
        state.Committed = combo.SelectedItem;
        state.Filtering = true;
        try
        {
            if (combo.ItemsSource != state.View && combo.ItemsSource != null)
            {
                state.View = new ListCollectionView(combo.ItemsSource.Cast<object>().ToList());
                combo.ItemsSource = state.View;
                combo.SelectedItem = state.Committed;
            }
            SetQuery(combo, "");
        }
        finally { state.Filtering = false; }
        combo.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (combo.IsDropDownOpen && combo.Template.FindName("PART_DropDownSearch", combo) is TextBox search)
            {
                search.PreviewKeyDown -= SearchKeyDown;
                search.PreviewKeyDown += SearchKeyDown;
                search.Focus();
                search.SelectAll();
            }
        });
    }
    static void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (((TextBox)sender).TemplatedParent is not ComboBox combo) return;
        if (e.Key == Key.Escape)
        {
            combo.IsDropDownOpen = false;
            combo.Focus();
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Enter)
        {
            if (combo.SelectedItem == null && combo.Items.Count > 0) combo.SelectedIndex = 0;
            if (e.Key == Key.Enter) combo.IsDropDownOpen = false;
            combo.Focus();
            e.Handled = true;
        }
    }
    static void Selected(object sender, SelectionChangedEventArgs e)
    {
        var combo = (ComboBox)sender;
        var state = States.GetOrCreateValue(combo);
        if (!state.Filtering && combo.SelectedItem != null) state.Committed = combo.SelectedItem;
    }
    static void QueryChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not ComboBox combo || !States.TryGetValue(combo, out var state) || state.View == null) return;
        string[] words = ((string?)e.NewValue ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool wasFiltering = state.Filtering;
        state.Filtering = true;
        try
        {
            state.View.Filter = item =>
            {
                string text = combo.DisplayMemberPath.Length == 0 ? item.ToString() ?? ""
                    : TypeDescriptor.GetProperties(item)[combo.DisplayMemberPath]?.GetValue(item)?.ToString() ?? "";
                return words.All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
            };
        }
        finally { state.Filtering = wasFiltering; }
    }
    static void Closed(object? sender, EventArgs e)
    {
        var combo = (ComboBox)sender!;
        if (!States.TryGetValue(combo, out var state)) return;
        object? selected = state.Committed;
        state.Filtering = true;
        try
        {
            SetQuery(combo, "");
            if (state.View != null) state.View.Filter = null;
            combo.SelectedItem = selected;
        }
        finally { state.Filtering = false; }
    }
}
