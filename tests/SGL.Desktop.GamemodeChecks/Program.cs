using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using SGL.Core;
using SGL.Desktop;

static class GamemodeDesktopChecks
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static object? Call(MainWindow w, string name, params object?[] args) => typeof(MainWindow).GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(w, args);
    static T Control<T>(MainWindow w, string name) => (T)w.FindName(name);
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {
        if (parent is Panel panel) foreach (UIElement child in panel.Children) { yield return child; foreach (var nested in Children(child)) yield return nested; }
        else if (parent is Decorator decorator && decorator.Child != null) { yield return decorator.Child; foreach (var nested in Children(decorator.Child)) yield return nested; }
    }
    [STAThread]
    static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var resources = XDocument.Load("src/SGL.Desktop/App.xaml").Root!.Elements().Single();
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(new XElement(XName.Get("ResourceDictionary", "http://schemas.microsoft.com/winfx/2006/xaml/presentation"), new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"), new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:SGL.Desktop;assembly=SGL"), resources.Elements()).ToString());
        string temp = Path.Combine(Path.GetTempPath(), "sgl-mode-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp); File.WriteAllText(Path.Combine(temp, "CloudSaveManifest.json"), "{\"Files\":[]}");
        var w = new MainWindow(["--data", Path.Combine(temp, "Data"), "--cloud-save", temp, "--no-update", "--no-mod-refresh"]);
        var tabs = Control<TabControl>(w, "Tabs");
        Check(ReferenceEquals(tabs.Items[0], Control<TabItem>(w, "HostingTab")), "Hosting is not the first navigation tab");
        var editorButtons = Children(Control<Grid>(w, "ModeEditor")).OfType<Button>().Select(b => b.Content as string).ToArray();
        Check(!editorButtons.Contains("Loadout") && !editorButtons.Contains("Export JSON"), "Removed editor buttons are still present");
        var rows = Control<StackPanel>(w, "Rows"); var categories = Control<ListBox>(w, "Categories"); var phases = Control<ComboBox>(w, "RulePhase");
        var search = Control<TextBox>(w, "SettingsSearch");
        foreach (var basis in BaseModes.All)
        {
            var payload = Configuration.New(basis.Id); string before = Json.Canonical(payload);
            Call(w, "Load", payload, null);
            Check(phases.Items.Count == 2, "Missing rule selector: " + basis.Name);
            foreach (RuleContext phase in phases.Items.Cast<RuleContext>().ToArray())
            {
                phases.SelectedValue = phase.Id;
                foreach (string category in categories.Items)
                {
                    categories.SelectedItem = category; Call(w, "Render");
                    var controls = Children(rows).ToArray();
                    var expected = GamemodeUi.Fields(payload, phase).Where(f => (string?)f["ui"]?["category"] == category).ToArray();
                    foreach (var field in expected)
                    {
                        string id = (string)field["id"]!;
                        if ((string?)field["type"] != "boolean")
                            Check(controls.Any(c => AutomationProperties.GetAutomationId(c) == id), "Missing rendered field: " + basis.Name + "/" + phase.Name + "/" + id);
                        string label = (string?)field["ui"]?["label"] ?? (string)field["label"]!;
                        Check(controls.OfType<TextBlock>().Any(t => t.Text == label), "Missing field label");
                    }
                }
            }
            var edited = (JsonObject)typeof(MainWindow).GetField("payload", Flags)!.GetValue(w)!;
            Check(Json.Canonical(edited) == before, "Rendering clamped or changed the template: " + basis.Name);
        }
        Call(w, "Load", Configuration.New("koth"), null);
        search.Text = "time to capture";
        Check(Children(rows).OfType<TextBox>().Any(t => AutomationProperties.GetAutomationId(t) == "modeTimeToCapture"), "Objective search failed");
        Check(!Children(rows).OfType<TextBlock>().Any(t => t.Text.Contains("SGL extra")), "In-game KOTH capture control labeled extra");
        search.Text = "bot difficulty";
        Check(Children(rows).OfType<TextBlock>().Any(t => t.Text.Contains("SGL extra")), "Hidden bot difficulty missing extra marker");
        // Preserve an imported objective component, but make its non-native status explicit for TDM.
        search.Clear(); var tdm = Configuration.New("tdm"); var koth = Configuration.New("koth");
        var capture = Configuration.Fields.Single(f => (string?)f["id"] == "modePointsOnCapture");
        string captureOption = (string)capture["option"]!;
        tdm["Options"]!["OptionMap"]![Configuration.Rules(tdm)[0].Id]!["Options"]![captureOption] = koth["Options"]!["OptionMap"]![Configuration.Rules(koth)[0].Id]!["Options"]![captureOption]!.DeepClone();
        Call(w, "Load", tdm, null); search.Text = "points on capture";
        Check(Children(rows).OfType<TextBox>().Any(t => AutomationProperties.GetAutomationId(t) == "modePointsOnCapture"), "Imported objective control was hidden");
        Check(Children(rows).OfType<TextBlock>().Any(t => t.Text.Contains("SGL extra") && t.Text.Contains("not native to Team Deathmatch")), "Non-native TDM capture setting missing explicit base marker");
        search.Clear();
        Call(w, "Load", Configuration.New("swat"), null); search.Text = "precision damage multiplier";
        Check(Children(rows).OfType<TextBox>().Any(t => t.Text == "10000"), "Stored SWAT multiplier was clamped");
        Check(Children(rows).OfType<TextBlock>().Any(t => t.Text.Contains("Outside in-game range")), "Out-of-range marker missing");
        search.Clear(); Call(w, "Load", Configuration.New("firecracker"), null); search.Text = "bomb explode time";
        Check(Children(rows).OfType<TextBox>().Single(t => AutomationProperties.GetAutomationId(t) == "modeBombExplodeTime").Text == "50", "Wrong standard Firecracker value");
        phases.SelectedIndex = 1;
        Check(Children(rows).OfType<TextBox>().Single(t => AutomationProperties.GetAutomationId(t) == "modeBombExplodeTime").Text == "15", "Wrong tiebreaker Firecracker value");
        var input = Children(rows).OfType<TextBox>().Single(t => AutomationProperties.GetAutomationId(t) == "modeBombExplodeTime"); input.Text = "25";
        phases.SelectedIndex = 0;
        Check(Children(rows).OfType<TextBox>().Single(t => AutomationProperties.GetAutomationId(t) == "modeBombExplodeTime").Text == "50", "Editing tiebreaker changed standard phase");
        Console.WriteLine("Passed desktop gamemode checks: every category/phase of all 88 presets, rendering preserves source values, objective search, per-mode extra markers, out-of-range indicators, and phase edits. Temporary fixtures only.");
        app.Shutdown();
    }
}
