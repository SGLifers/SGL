using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using SGL.Core;
using SGL.Desktop;

static class HostingDesktopChecks
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static object? Call(object target, string method, params object?[] args) => target.GetType().GetMethod(method, Flags | BindingFlags.Public)!.Invoke(target, args);
    static void Set(MainWindow w, string name, object? value) => typeof(MainWindow).GetField(name, Flags)!.SetValue(w, value);
    static T Get<T>(MainWindow w, string name) => (T)(typeof(MainWindow).GetField(name, Flags)?.GetValue(w) ?? typeof(MainWindow).GetProperty(name, Flags)!.GetValue(w))!;
    static T Control<T>(MainWindow w, string name) => (T)w.FindName(name);
    static void Check(bool result, string message) { if (!result) throw new Exception(message); }
    [STAThread]
    static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var resources = XDocument.Load("src/SGL.Desktop/App.xaml").Root!.Elements().Single();
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(new XElement(XName.Get("ResourceDictionary", "http://schemas.microsoft.com/winfx/2006/xaml/presentation"), new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"), new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:SGL.Desktop;assembly=SGL"), resources.Elements()).ToString());
        var task = Run(); var frame = new DispatcherFrame(); task.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false)); Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult(); app.Shutdown();
        Console.WriteLine("Passed Hosting desktop checks: spinner presets/editing/persistence, five sections, debounce, identity changes, duplicate protection, retry identity, canceled/stale responses, deployment-disabled state and narrow layout. No real game or Discord writes.");
    }
    static async Task Run()
    {
        string temp = Path.Combine(Path.GetTempPath(), "SGL-hosting-ui-" + Guid.NewGuid().ToString("N")), data = Path.Combine(temp, "Data"), cloud = Path.Combine(temp, "CloudSave");
        Directory.CreateDirectory(cloud); File.WriteAllText(Path.Combine(cloud, "CloudSaveManifest.json"), "{\"Files\":[]}");
        var w = new MainWindow(["--data", data, "--cloud-save", cloud, "--no-update", "--no-mod-refresh"]);
        Set(w, "hostingEndpointOverride", "");
        Check(Control<TabControl>(w, "Tabs").Items.Count == 5, "Hosting tab missing");
        Set(w, "changingContentView", true); Control<TabControl>(w, "ModeViews").SelectedIndex = 1; Set(w, "changingContentView", false); Control<TabItem>(w, "HostingTab").IsSelected = true;
        Check(!Get<bool>(w, "RemoteViewSelected"), "Hosting entered the mode community view");
        var presets = Control<ComboBox>(w, "HostingPreset"); Check(presets.Items.Count == 2, "Spinner defaults missing");
        presets.SelectedItem = "Quick Play";
        Control<TextBox>(w, "HostingPresetName").Text = "Friday event"; Call(w, "CopyHostingPreset", w, new RoutedEventArgs());
        Check((string)presets.SelectedItem == "Friday event" && presets.Items.Count == 3, "Rotation preset copy failed");
        Control<TextBox>(w, "HostingModesInput").Text = "Custom A\nCustom B"; Call(w, "SaveHostingModes", w, new RoutedEventArgs());
        Control<TextBox>(w, "HostingMapListName").Text = "Custom maps"; Control<TextBox>(w, "HostingMapsInput").Text = "Map A\nMap B"; Call(w, "SaveHostingMapList", w, new RoutedEventArgs());
        Control<ComboBox>(w, "HostingAssignedMode").SelectedItem = "Custom A"; Control<ListBox>(w, "HostingAssignedLists").SelectedItems.Add("Custom maps"); Call(w, "SaveHostingLinks", w, new RoutedEventArgs());
        string? previous = null;
        for (int i = 0; i < 10; i++)
        {
            Call(w, "HostingSpinClick", w, new RoutedEventArgs());
            var spin = Get<HostingSpin>(w, "hostingResult"); Check(spin.Gamemode == "Custom A" && spin.Map != previous && spin.Map is "Map A" or "Map B", "Spinner ignored links or immediate-map cooldown"); previous = spin.Map;
        }
        var saved = HostingRotation.Read(File.ReadAllText(Path.Combine(data, "Hosting", "rotations.json")));
        Check(saved["Friday event"].Gamemodes.SequenceEqual(new[] { "Custom A", "Custom B" }) && saved["Quick Play"].Gamemodes.Count == 10, "Spinner persistence mutated the source preset");
        Control<TextBox>(w, "HostingModesInput").Clear(); Call(w, "SaveHostingModes", w, new RoutedEventArgs());
        Check((string)presets.SelectedItem == "Friday event" && Control<TextBox>(w, "HostingModesInput").Text == "", "Invalid edit lost selection or input");
        var code = Control<TextBox>(w, "HostingLobbyCode");
        var lobbyName = Control<TextBox>(w, "HostingLobbyName");
        Check(code.MaxLength == 6 && code.Width == 160 && lobbyName.MaxLength == 80, "Lobby inputs have incorrect limits or code width");
        code.Text = "ABC123";
        Check(!Get<DispatcherTimer>(w, "hostingAnnouncementTimer").IsEnabled && Control<TextBlock>(w, "HostingAnnouncementStatus").Text.Contains("after deployment"), "Unconfigured app attempted to broadcast");
        using var http = new FakeHosting(); Set(w, "hostingTransport", http); Set(w, "hostingEndpointOverride", "https://bot.example/");
        void Session(string actor, char tokenLetter = 't')
        {
            string root = Get<string>(w, "LibraryRoot"); var session = Call(w, "Session", root)!;
            Call(session, "Save", new JsonObject { ["token"] = new string(tokenLetter, 43), ["expiresAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600, ["user"] = new JsonObject { ["id"] = actor, ["displayName"] = "Fixture Host", ["discordId"] = "123456789012345678" } });
            Call(w, "RefreshSharingAccount");
        }
        Session(new string('a', 32)); Check(code.Text == "", "Sign-in replayed an old lobby code");
        lobbyName.Text = "Friday night";
        code.Text = "ABC12"; await Task.Delay(2100); Check(http.Calls.Count == 0, "Incomplete five-character code was announced");
        code.Text = "ABC123"; await Task.Delay(200); Check(http.Calls.Count == 0, "Code broadcast before debounce");
        code.Text = "ABC124"; lobbyName.Text = "Friday games"; await Task.Delay(2300);
        Check(http.Calls.Count == 1 && (string?)http.Calls[0]["code"] == "ABC124", "Typing broadcast partial/stale codes");
        Check((string?)http.Calls[0]["lobbyName"] == "Friday games", "Announcement omitted the latest lobby name");
        Check(Control<TextBlock>(w, "HostingAnnouncementStatus").Text == "Lobby announced in Discord.", "Announcement confirmation missing");
        code.Text = " ABC124 "; await Task.Delay(2100); Check(http.Calls.Count == 1, "Normalized duplicate code was announced twice");
        lobbyName.Text = "Next lobby"; await Task.Delay(2100); Check(http.Calls.Count == 1, "Name edit duplicated an announced lobby");
        Session(new string('a', 32), 'u'); Check(code.Text == "" && lobbyName.Text == "", "Reauthentication retained lobby details from an old session");
        Session(new string('b', 32)); Check(code.Text == "", "Account switch retained another host's code");
        code.Text = "RETRY1"; http.RateLimitOnce = true; await (Task)Call(w, "SendHostingAnnouncement")!; await Task.Delay(1200);
        var retries = http.Calls.Where(c => (string?)c["code"] == "RETRY1").ToArray();
        Check(retries.Length == 2 && (string?)retries[0]["requestId"] == (string?)retries[1]["requestId"], "Automatic retry changed its request identity");
        http.Hold = new TaskCompletionSource<HttpResponseMessage>(); code.Text = "SLOW12";
        var pending = (Task)Call(w, "SendHostingAnnouncement")!; await Task.Delay(20); code.Clear();
        http.Hold.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"sent\"}") }); await pending; http.Hold = null;
        Check(Control<TextBlock>(w, "HostingAnnouncementStatus").Text.StartsWith("No announcement pending"), "Stale response replaced the cleared-code status");
        Check(Control<Button>(w, "HostingSignInButton").Visibility == Visibility.Collapsed, "Sign-in button remained visible for a signed-in host");
        Call(w, "RenderHostingRotation");
        var rootElement = (FrameworkElement)w.Content; w.Content = null;
        var render = new Border { Child = rootElement, Background = (Brush)new BrushConverter().ConvertFromString("#10152B")! }; System.Windows.Documents.TextElement.SetForeground(render, Brushes.White); System.Windows.Documents.TextElement.SetFontFamily(render, new FontFamily("Segoe UI")); System.Windows.Documents.TextElement.SetFontSize(render, 14);
        render.Measure(new Size(1350, 900)); render.Arrange(new Rect(0, 0, 1350, 900)); render.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1350, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(render); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); Directory.CreateDirectory("artifacts"); using (var file = File.Create("artifacts/sgl-hosting.png")) png.Save(file);
        render.Measure(new Size(1000, 700)); render.Arrange(new Rect(0, 0, 1000, 700)); render.UpdateLayout();
        Check(code.ActualWidth == 160 && lobbyName.ActualWidth > 350 && lobbyName.ActualWidth + code.ActualWidth < 850, "Lobby inputs exceed narrow workspace or the code box stretches");
        render.Child = null; w.Content = rootElement; w.Close();
    }
    sealed class FakeHosting : HttpMessageHandler
    {
        public List<JsonObject> Calls = []; public bool RateLimitOnce; public TaskCompletionSource<HttpResponseMessage>? Hold;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(Json.Read(await request.Content!.ReadAsStringAsync()).AsObject());
            if (Hold != null) return await Hold.Task;
            if (RateLimitOnce) { RateLimitOnce = false; return new(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"retryAfter\":1}") }; }
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"sent\",\"messageId\":\"123456789012345678\"}") };
        }
    }
}
