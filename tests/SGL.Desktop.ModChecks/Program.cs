using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using SGL.Core;
using SGL.Desktop;

static class DesktopChecks
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static object? Call(MainWindow w, string method, params object?[] args) => typeof(MainWindow).GetMethod(method, Flags)!.Invoke(w, args);
    static void Set(MainWindow w, string field, object? value) => typeof(MainWindow).GetField(field, Flags)!.SetValue(w, value);
    static T Get<T>(MainWindow w, string field) => (T)(typeof(MainWindow).GetField(field, Flags)?.GetValue(w) ?? typeof(MainWindow).GetProperty(field, Flags)!.GetValue(w))!;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static T Named<T>(MainWindow w, string name) => (T)w.FindName(name);
    [STAThread]
    static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var source = XDocument.Load("src/SGL.Desktop/App.xaml").Root!.Elements().Single();
        var dictionary = new XElement(XName.Get("ResourceDictionary", "http://schemas.microsoft.com/winfx/2006/xaml/presentation"),
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:SGL.Desktop;assembly=SGL"), source.Elements());
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString());
        var task = Run();
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
        app.Shutdown();
        Console.WriteLine("Passed Mods desktop checks: five sections, discovery, offline fallback, future mods, search, configuration editing, toggles, updates, runtime status, navigation isolation and narrow layout. Temporary files and fake HTTP only.");
    }
    static async Task Run()
    {
        string temp = Path.Combine(Path.GetTempPath(), "SGL-mod-ui-" + Guid.NewGuid().ToString("N"));
        string cloud = Path.Combine(temp, "CloudSave"), data = Path.Combine(temp, "Data"), game = Path.Combine(temp, "Game");
        Directory.CreateDirectory(cloud); Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(cloud, "CloudSaveManifest.json"), "{\"Files\":[]}");
        File.WriteAllText(Path.Combine(game, GameInstallation.Executable), "UI game fixture");
        byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ue4ss/Mods/BotChatter/Scripts/main.lua"] = Bytes("-- UI script fixture"),
            ["ue4ss/Mods/BotChatter/config.json"] = Bytes("{\"host_only\":true,\"death_chat_chance\":0.3}"),
            ["ue4ss/Mods/BotChatter/phrases.json"] = Bytes("{\"on_death\":[\"hello\"]}"),
            ["ue4ss/Mods/BotChatter/bot_names.json"] = Bytes("[\"Fixture\"]"),
            ["ue4ss/Mods/MapEditorBudgetMod/scripts/main.lua"] = Bytes("-- budget fixture"),
            ["ue4ss/Mods/MapEditorBudgetMod/scripts/config.lua"] = Bytes("return { BudgetLimit = 10000 }")
        };
        var bundle = new ModBundle(new("v1.1.0", Mods.BootstrapUrl, Mods.BootstrapHash, Mods.Discover(files)), files);
        var store = new ModStore(game, data, () => false);
        store.Install(bundle, "BotChatter", new(StringComparer.OrdinalIgnoreCase) { ["dwmapi.dll"] = [1], ["ue4ss/UE4SS.dll"] = [2], ["ue4ss/Mods/mods.txt"] = Bytes("Keybinds : 1\n") });
        store.Toggle("BotChatter", false);
        var window = new MainWindow(["--data", data, "--cloud-save", cloud, "--no-update", "--no-mod-refresh"]);
        Set(window, "modGameRunning", (Func<bool>)(() => false)); Call(window, "SetModGame", game);
        var tabs = Named<TabControl>(window, "Tabs");
        Check(tabs.Items.Count == 5, "Mods/Hosting section missing or unexpected extra tabs");
        // Enter Mods while the mode library has Community selected; no sharing context should move.
        Set(window, "changingContentView", true); Named<TabControl>(window, "ModeViews").SelectedIndex = 1; Set(window, "changingContentView", false);
        string browseBefore = Get<string>(window, "browseKind");
        Named<TabItem>(window, "ModsTab").IsSelected = true;
        Check(!Get<bool>(window, "RemoteViewSelected") && Get<string>(window, "browseKind") == browseBefore, "Mods navigation entered the mode community view");
        using var handler = new CatalogHandler(files);
        Set(window, "modsTransport", handler);
        await (Task)Call(window, "DiscoverMods")!;
        var list = Named<ListBox>(window, "ModList");
        Check(list.Items.Count == 2, "Community mods missing from the UI");
        list.SelectedIndex = 0;
        Check(Named<Button>(window, "EnableModButton").IsEnabled && !Named<Button>(window, "DisableModButton").IsEnabled, "Disabled mod did not offer Enable");
        Check(Named<StackPanel>(window, "ModConfigurationPanel").Visibility == Visibility.Visible && Named<ComboBox>(window, "ModConfigFiles").Items.Count == 3, "Bot configuration files missing");
        Check(Named<TextBlock>(window, "ModRuntimeStatus").Text.Contains("installed"), "Installed runtime status missing");
        var editor = Named<TextBox>(window, "ModConfigText");
        var configs = Named<ComboBox>(window, "ModConfigFiles");
        configs.SelectedItem = configs.Items.Cast<ModConfiguration>().Single(c => c.Path.EndsWith("/config.json"));
        editor.Text = editor.Text.Replace("0.3", "0.75");
        Check(Named<Button>(window, "SaveModConfigButton").IsEnabled && !Named<Button>(window, "EnableModButton").IsEnabled, "Unsaved configuration did not protect mod actions");
        // Refresh must retain an unsaved editor, even as future mods arrive.
        handler.Files["ue4ss/Mods/NewCommunityMod/Scripts/main.lua"] = Bytes("-- discovered after SGL was built");
        handler.Version = "v1.2.0";
        await (Task)Call(window, "DiscoverMods")!;
        Check(list.Items.Count == 3 && editor.Text.Contains("0.75") && Get<bool>(window, "modConfigDirty"), "Discovery lost configuration edits or required a hardcoded mod list");
        Call(window, "SaveModConfiguration", window, new RoutedEventArgs()); await Idle(window);
        Check(store.Configurations("BotChatter").Any(c => c.Text.Contains("0.75")) && !Get<bool>(window, "modConfigDirty"), "Configuration editor failed to save");
        Check(Named<Button>(window, "UpdateModButton").IsEnabled, "New release did not offer an update");
        await (Task)Call(window, "InstallSelectedMod")!;
        Check(store.Installed().Single().Version == "v1.2.0" && !store.Enabled("BotChatter"), "Update changed the disabled state");
        Call(window, "EnableModClick", window, new RoutedEventArgs()); await Idle(window);
        Check(store.Enabled("BotChatter") && Named<Button>(window, "DisableModButton").IsEnabled, "Enable UI failed");
        Call(window, "DisableModClick", window, new RoutedEventArgs()); await Idle(window);
        Check(!store.Enabled("BotChatter"), "Disable UI failed");
        Named<TextBox>(window, "ModSearch").Text = "NewCommunity";
        Check(list.Items.Count == 1, "Mod search failed"); list.SelectedIndex = 0;
        Check(Named<Button>(window, "EnableModButton").Content.ToString() == "Install & enable", "Future mod did not offer generic installation");
        handler.Offline = true;
        await (Task)Call(window, "DiscoverMods")!;
        Check(Named<TextBlock>(window, "ModsStatus").Text.StartsWith("Offline catalog"), "Failed refresh lost offline browsing");
        Named<TextBox>(window, "ModSearch").Clear(); list.SelectedIndex = 0;
        var root = (FrameworkElement)window.Content; window.Content = null;
        var render = new Border { Child = root, Background = (Brush)new BrushConverter().ConvertFromString("#10152B")! };
        System.Windows.Documents.TextElement.SetForeground(render, Brushes.White);
        System.Windows.Documents.TextElement.SetFontFamily(render, new FontFamily("Segoe UI"));
        System.Windows.Documents.TextElement.SetFontSize(render, 14);
        render.Measure(new Size(1350, 900)); render.Arrange(new Rect(0, 0, 1350, 900)); render.UpdateLayout();
        string output = "artifacts/sgl-mods.png"; Directory.CreateDirectory("artifacts");
        var bitmap = new RenderTargetBitmap(1350, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(render);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(output)) png.Save(stream);
        render.Measure(new Size(1000, 700)); render.Arrange(new Rect(0, 0, 1000, 700)); render.UpdateLayout();
        Check(Named<Grid>(window, "ModsWorkspace").ActualWidth <= 850 && Named<Grid>(window, "ModsWorkspace").ActualWidth > 600, "Mods layout exceeds the narrow workspace: " + Named<Grid>(window, "ModsWorkspace").ActualWidth);
        render.Child = null; window.Content = root;
        window.Close();
    }
    static async Task Idle(MainWindow window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (Get<bool>(window, "modsBusy") && DateTime.UtcNow < deadline) await Task.Delay(10);
        Check(!Get<bool>(window, "modsBusy"), "Mod operation did not complete");
    }
    sealed class CatalogHandler(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files = new(files); public string Version = "v1.1.0"; public bool Offline;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline) throw new HttpRequestException("Offline fixture");
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true)) foreach (var p in Files)
            {
                var entry = zip.CreateEntry(p.Key); entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var output = entry.Open(); output.Write(p.Value);
            }
            byte[] bytes = stream.ToArray();
            var metadata = new JsonObject { ["tag_name"] = Version, ["assets"] = new JsonArray(new JsonObject { ["name"] = "Splitgate-Mods.zip", ["digest"] = "sha256:" + Json.Hash(bytes), ["browser_download_url"] = Mods.BootstrapUrl.Replace("v1.1.0", Version) }) };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = request.RequestUri!.AbsoluteUri == Mods.Latest ? new StringContent(metadata.ToJsonString()) : new ByteArrayContent(bytes) });
        }
    }
}
