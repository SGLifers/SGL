using System.Windows;
using System.Windows.Controls;
namespace SGL.Desktop;

public partial class MainWindow
{
    readonly WrapPanel loadoutTools = new() { Margin = new Thickness(0, 0, 0, 12) };
    readonly CheckBox LockPrimary = new() { Name = "LockPrimary", Content = "Lock primary" };
    readonly CheckBox LockSecondary = new() { Name = "LockSecondary", Content = "Lock secondary" };
    readonly CheckBox LockEquipment = new() { Name = "LockEquipment", Content = "Lock equipment" };
    void InitializeLoadoutTools()
    {
        var randomize = new Button { Content = "Randomize loadout" };
        randomize.Click += Randomize;
        loadoutTools.Children.Add(randomize);
        foreach (var checkbox in new[] { LockPrimary, LockSecondary, LockEquipment })
        {
            RegisterName(checkbox.Name, checkbox);
            loadoutTools.Children.Add(checkbox);
        }
    }
}
