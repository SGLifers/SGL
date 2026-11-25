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
        Console.WriteLine("Passed Hosting desktop checks: spinner presets/editing/persistence, five sections, explicit open/close actions, identity changes, duplicate protection, manual retry identity, canceled/stale responses, deployment-disabled state and narrow layout. No real game or Discord writes.");
    }
    static async Task Run()
    {
        string temp = Path.Combine(Path.GetTempPath(), "SGL-hosting-ui-" + Guid.NewGuid().ToString("N")), data = Path.Combine(temp, "Data"), cloud = Path.Combine(temp, "CloudSave");
        Directory.CreateDirectory(cloud); File.WriteAllText(Path.Combine(cloud, "CloudSaveManifest.json"), "{\"Files\":[]}");
        var w = new MainWindow(["--data", data, "--cloud-save", cloud, "--no-update", "--no-mod-refresh"]);
        Set(w, "hostingEndpointOverride", "");
        Check(Control<TabControl>(w, "Tabs").Items.Count == 5 && ReferenceEquals(Control<TabControl>(w, "Tabs").Items[0], Control<TabItem>(w, "HostingTab")), "Hosting must be the first tab");
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
        var modeSource = Control<ComboBox>(w, "HostingGamemodeSource");
        var customMode = Control<TextBox>(w, "HostingCustomGamemode");
        Check(modeSource.Items.Count == 3 && modeSource.SelectedIndex == 0 && Control<StackPanel>(w, "HostingCustomGamemodePanel").Visibility == Visibility.Collapsed, "Gamemode source must default to Unset");
        var code = Control<TextBox>(w, "HostingLobbyCode");
        var lobbyName = Control<TextBox>(w, "HostingLobbyName");
        Check(code.MaxLength == 6 && code.Width == 160 && lobbyName.MaxLength == 80, "Lobby inputs have incorrect limits or code width");
        code.Text = "ABC123";
        Check(!Control<Button>(w, "HostingOpenLobbyButton").IsEnabled && !Control<Button>(w, "HostingCloseLobbyButton").IsEnabled, "Unconfigured app enabled lobby controls");
        using var http = new FakeHosting(); Set(w, "hostingTransport", http); Set(w, "hostingEndpointOverride", "https://bot.example/");
        void Session(string actor, char tokenLetter = 't')
        {
            string root = Get<string>(w, "LibraryRoot"); var session = Call(w, "Session", root)!;
            Call(session, "Save", new JsonObject { ["token"] = new string(tokenLetter, 43), ["expiresAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600, ["user"] = new JsonObject { ["id"] = actor, ["displayName"] = "Fixture Host", ["discordId"] = "123456789012345678" } });
            Call(w, "RefreshSharingAccount");
        }
        Session(new string('a', 32)); Check(code.Text == "", "Sign-in replayed an old lobby code");
        var open = Control<Button>(w, "HostingOpenLobbyButton");
        var close = Control<Button>(w, "HostingCloseLobbyButton");
        lobbyName.Text = "Friday night";
        code.Text = "ABC12"; Check(!open.IsEnabled && !close.IsEnabled, "Incomplete code enabled lobby controls");
        code.Text = "ABC123"; lobbyName.Text = "Friday games"; await Task.Delay(2200);
        Check(http.Calls.Count == 0 && open.IsEnabled && close.IsEnabled, "Typing announced a lobby or failed to enable buttons");
        await (Task)Call(w, "SendHostingAnnouncement", "open")!;
        Check(http.Calls.Count == 1 && (string?)http.Calls[0]["code"] == "ABC123" && (string?)http.Calls[0]["action"] == "open", "Open Lobby did not send the chosen code");
        Check((string?)http.Calls[0]["lobbyName"] == "Friday games" && http.Calls[0]["gamemode"] == null, "Unset must omit the gamemode");
        Check(!open.IsEnabled && close.IsEnabled && code.IsReadOnly && lobbyName.IsReadOnly, "Open lobby did not lock its details");
        await (Task)Call(w, "SendHostingAnnouncement", "open")!;
        Check(http.Calls.Count == 1, "Repeated Open Lobby created another request");
        await (Task)Call(w, "SendHostingAnnouncement", "close")!;
        Check((string?)http.Calls[1]["action"] == "close" && (string?)http.Calls[1]["code"] == "ABC123", "Close Lobby targeted the wrong action/code");
        Check(Control<TextBlock>(w, "HostingAnnouncementStatus").Text == "Lobby closed in Discord." && open.IsEnabled && !code.IsReadOnly, "Close confirmation or field unlock missing");
        await (Task)Call(w, "SendHostingAnnouncement", "open")!;
        Check((string?)http.Calls[0]["requestId"] != (string?)http.Calls[2]["requestId"], "Reopening reused an old open request identity");
        http.FailOnce = HttpStatusCode.NotFound;
        await (Task)Call(w, "SendHostingAnnouncement", "close")!;
        Check(open.IsEnabled && !code.IsReadOnly && Control<TextBlock>(w, "HostingAnnouncementStatus").Text.Contains("No recent announcement"), "Expired server receipt left lobby details permanently locked");
        Session(new string('a', 32), 'u'); Check(code.Text == "" && lobbyName.Text == "" && !code.IsReadOnly, "Reauthentication retained lobby details from an old session");
        Session(new string('b', 32)); Check(code.Text == "", "Account switch retained another host's code");
        code.Text = "RETRY1"; http.RateLimitOnce = true;
        await (Task)Call(w, "SendHostingAnnouncement", "open")!; await Task.Delay(1200);
        Check(http.Calls.Count(c => (string?)c["code"] == "RETRY1") == 1, "A failed lobby request retried without a button click");
        await (Task)Call(w, "SendHostingAnnouncement", "open")!;
        var retries = http.Calls.Where(c => (string?)c["code"] == "RETRY1").ToArray();
        Check(retries.Length == 2 && (string?)retries[0]["requestId"] == (string?)retries[1]["requestId"], "Manual retry changed its request identity");
        await (Task)Call(w, "SendHostingAnnouncement", "close")!;
        code.Text = "MODE12"; modeSource.SelectedIndex = 2; customMode.Text = "Custom SWAT";
        Check(Control<StackPanel>(w, "HostingCustomGamemodePanel").Visibility == Visibility.Visible, "Custom text input missing");
        await (Task)Call(w, "SendHostingAnnouncement", "open")!;
        Check((string?)http.Calls[^1]["gamemode"] == "Custom SWAT", "Custom gamemode omitted from open");
        int beforeTyping = http.Calls.Count; customMode.Text = "Custom KOTH"; Control<TextBox>(w, "HostingLobbyMap").Text = "Manual map";
        Check(http.Calls.Count == beforeTyping && Control<Button>(w, "HostingUpdateGamemodeButton").IsEnabled, "Custom typing sent automatically or disabled update");
        await (Task)Call(w, "SendHostingAnnouncement", "update")!;
        Check((string?)http.Calls[^1]["gamemode"] == "Custom KOTH" && http.Calls[^1]["announcementId"] != null, "Custom edit failed to target original announcement");
        Check((string?)http.Calls[^1]["map"] == "Manual map", "Manual map was omitted from details update");
        customMode.Text = "Retry mode"; http.FailOnce = HttpStatusCode.ServiceUnavailable;
        await (Task)Call(w, "SendHostingAnnouncement", "update")!; string? updateRetryId = (string?)http.Calls[^1]["requestId"];
        await (Task)Call(w, "SendHostingAnnouncement", "update")!;
        Check((string?)http.Calls[^1]["requestId"] == updateRetryId, "Gamemode retry changed its identity");
        int beforeSpin = http.Calls.Count; await (Task)Call(w, "SpinHosting")!;
        Check(http.Calls.Count == beforeSpin + 1 && (string?)http.Calls[^1]["gamemode"] == "Retry mode" && (string?)http.Calls[^1]["map"] == Get<HostingSpin>(w, "hostingResult").Map, "Spin must update the map while preserving Custom gamemode");
        modeSource.SelectedIndex = 0; await (Task)Call(w, "SendHostingAnnouncement", "update")!;
        Check((string?)http.Calls[^1]["gamemode"] == "", "Unset update did not remove gamemode");
        modeSource.SelectedIndex = 1; await (Task)Call(w, "SpinHosting")!;
        Check((string?)http.Calls[^1]["action"] == "update" && (string?)http.Calls[^1]["gamemode"] == Get<HostingSpin>(w, "hostingResult").Gamemode, "SpinGate did not update the same card");
        http.Hold = new TaskCompletionSource<HttpResponseMessage>();
        var spinning = (Task)Call(w, "SpinHosting")!; await Task.Delay(20); int updatingCount = http.Calls.Count;
        await (Task)Call(w, "SpinHosting")!;
        Check(http.Calls.Count == updatingCount && !Control<Button>(w, "HostingSpinButton").IsEnabled, "Concurrent spins sent out-of-order updates");
        http.Hold.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"updated\",\"lobbyStatus\":\"open\",\"announcementId\":\"" + http.CurrentAnnouncement + "\"}") }); await spinning; http.Hold = null;
        await (Task)Call(w, "SendHostingAnnouncement", "close")!;
        int afterClose = http.Calls.Count; await (Task)Call(w, "SpinHosting")!;
        Check(http.Calls.Count == afterClose, "Spin reopened a closed lobby");
        Session(new string('g', 32));
        Check(modeSource.SelectedIndex == 0 && customMode.Text == "" && Control<TextBox>(w, "HostingLobbyMap").Text == "", "Account change retained old lobby details");
        http.Hold = new TaskCompletionSource<HttpResponseMessage>(); code.Text = "SLOW12";
        var pending = (Task)Call(w, "SendHostingAnnouncement", "open")!; await Task.Delay(20);
        Check(!open.IsEnabled && !close.IsEnabled && code.IsReadOnly, "In-flight lobby request left controls enabled");
        code.Clear();
        http.Hold.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"sent\"}") }); await pending; http.Hold = null;
        Check(Control<TextBlock>(w, "HostingAnnouncementStatus").Text.StartsWith("Enter your lobby code"), "Stale response replaced the cleared-code status");
        http.Hold = new TaskCompletionSource<HttpResponseMessage>(); code.Text = "SLOW13";
        pending = (Task)Call(w, "SendHostingAnnouncement", "open")!; await Task.Delay(20);
        Session(new string('c', 32));
        http.Hold.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"sent\"}") }); await pending; http.Hold = null;
        Check(code.Text == "" && !open.IsEnabled && !close.IsEnabled && !code.IsReadOnly, "Late response crossed a session change");
        lobbyName.Text = "Friday games"; code.Text = "ABC123";
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
        public List<JsonObject> Calls = []; public string? CurrentAnnouncement; public bool RateLimitOnce; public HttpStatusCode? FailOnce; public TaskCompletionSource<HttpResponseMessage>? Hold;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(Json.Read(await request.Content!.ReadAsStringAsync()).AsObject());
            if (Hold != null) return await Hold.Task;
            if (FailOnce is HttpStatusCode status) { FailOnce = null; return new(status) { Content = new StringContent("{}") }; }
            if (RateLimitOnce) { RateLimitOnce = false; return new(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"retryAfter\":1}") }; }
            string? action = (string?)Calls[^1]["action"];
            bool closed = action == "close";
            if (action == "open") CurrentAnnouncement = (string?)Calls[^1]["requestId"];
            return new(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["status"] = closed ? "closed" : action == "update" ? "updated" : "sent", ["lobbyStatus"] = closed ? "closed" : "open", ["messageId"] = "123456789012345678", ["announcementId"] = CurrentAnnouncement }.ToJsonString()) };
        }
    }
}
