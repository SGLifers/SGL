using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using SGL.Core;
namespace SGL.Desktop;

public partial class MainWindow
{
    Dictionary<string, HostingRotation> hostingRotations = [];
    readonly HostingSpinner hostingSpinner = new();
    HostingSpin? hostingResult;
    bool hostingReady, renderingHosting;
    CancellationTokenSource? hostingAnnouncementRequest;
    HttpMessageHandler? hostingTransport = null;
    string? hostingEndpointOverride = null;
    string? hostingActor;
    string? hostingSessionKey;
    string hostingOpenCode = "";
    string? hostingAnnouncementId;
    string HostingGamemodeSourceValue => (string?)((ComboBoxItem?)HostingGamemodeSource.SelectedItem)?.Tag ?? "unset";
    string HostingGamemode => HostingAnnouncements.Gamemode(HostingGamemodeSourceValue == "custom" ? HostingCustomGamemode.Text : HostingGamemodeSourceValue == "spingate" ? hostingResult?.Gamemode ?? "" : "");
    void HostingGamemodeChanged(object sender, RoutedEventArgs e)
    {
        if (!hostingReady || renderingHosting) return;
        hostingGeneration++; hostingAnnouncementRequest?.Cancel();
        RefreshHostingLobbyButtons();
        if (hostingOpenCode.Length > 0) HostingAnnouncementStatus.Text = HostingGamemodeSourceValue == "spingate" ? "Spin to update the open lobby’s gamemode, or choose Update details." : "Choose Update details to apply this selection to the open lobby.";
    }
    bool hostingLobbyBusy;
    int hostingGeneration;
    readonly Dictionary<string, string> hostingRequestIds = [];
    string HostingRotationsPath => Path.Combine(dataRoot, "Hosting", "rotations.json");
    HostingRotation CurrentHostingRotation => hostingRotations[(string)HostingPreset.SelectedItem];
    string HostingEndpoint
    {
        get
        {
            if (hostingEndpointOverride != null) return hostingEndpointOverride;
            return (string?)SGL.Core.Json.Read(Modes.Resource("hosting-service.json"))["apiUrl"] ?? "";
        }
    }
    void InitializeHosting()
    {
        string? notice = null;
        try { hostingRotations = File.Exists(HostingRotationsPath) ? HostingRotation.Read(File.ReadAllText(HostingRotationsPath)) : HostingRotation.Defaults(); }
        catch { hostingRotations = HostingRotation.Defaults(); notice = "Saved rotations could not be read. Built-ins are loaded; the original file has been kept."; }
        renderingHosting = true;
        HostingPreset.ItemsSource = hostingRotations.Keys.ToArray();
        HostingPreset.SelectedItem = (string?)Preferences()["hostingPreset"] is string saved && hostingRotations.ContainsKey(saved) ? saved : hostingRotations.Keys.First();
        renderingHosting = false; hostingReady = true;
        RenderHostingRotation(); RefreshHostingAccount();
        if (notice != null) HostingSpinStatus.Text = notice;
        Closed += (_, _) => hostingAnnouncementRequest?.Cancel();
    }
    void RefreshHostingAccount()
    {
        if (!hostingReady) return;
        var session = Session(LibraryRoot).Load();
        string? actor = (string?)session?["user"]?["id"];
        string? sessionKey = (string?)session?["token"] is string token ? SGL.Core.Json.Hash(System.Text.Encoding.UTF8.GetBytes(token)) : null;
        if (actor != hostingActor || sessionKey != hostingSessionKey)
        {
            hostingActor = actor; hostingSessionKey = sessionKey; hostingRequestIds.Clear(); hostingOpenCode = ""; hostingAnnouncementId = null;
            hostingGeneration++; hostingAnnouncementRequest?.Cancel(); hostingAnnouncementRequest = null; hostingLobbyBusy = false;
            HostingLobbyCode.Clear();
            HostingLobbyName.Clear();
            HostingGamemodeSource.SelectedIndex = 0; HostingCustomGamemode.Clear(); HostingLobbyMap.Clear();
        }
        HostingAccount.Text = actor == null ? "Not signed in" : "Signed in as " + (string?)session?["user"]?["displayName"];
        HostingSignInButton.Visibility = actor == null ? Visibility.Visible : Visibility.Collapsed;
        if (HostingLobbyCode.Text.Length == 0) HostingAnnouncementStatus.Text = string.IsNullOrWhiteSpace(HostingEndpoint) ? "Lobby announcements will be available after the bot is deployed." : actor == null ? "Sign in with Discord to announce your lobby." : "Enter your lobby code, then choose Open Lobby.";
        RefreshHostingLobbyButtons();
    }
    async void HostingSignIn(object sender, RoutedEventArgs e)
    {
        HostingAnnouncementStatus.Text = "Complete Discord sign-in in your browser.";
        await SignInDiscord(); RefreshHostingAccount();
        if (hostingActor == null) HostingAnnouncementStatus.Text = LibraryStatus.Text;
    }
    void HostingCodeChanged(object sender, TextChangedEventArgs e)
    {
        if (!hostingReady) return;
        hostingGeneration++; hostingAnnouncementRequest?.Cancel();
        RefreshHostingLobbyButtons();
        if (string.IsNullOrWhiteSpace(HostingLobbyCode.Text)) { HostingAnnouncementStatus.Text = "Enter your lobby code, then choose Open Lobby. Existing Discord announcements are unchanged."; return; }
        try { HostingAnnouncements.Code(HostingLobbyCode.Text); HostingAnnouncements.LobbyName(HostingLobbyName.Text); } catch (Exception error) { HostingAnnouncementStatus.Text = error.Message; return; }
        if (string.IsNullOrWhiteSpace(HostingEndpoint)) { HostingAnnouncementStatus.Text = "Not announced. The bot endpoint will be connected after deployment."; return; }
        if (hostingActor == null) { HostingAnnouncementStatus.Text = "Sign in with Discord to open or close your lobby announcement."; return; }
        HostingAnnouncementStatus.Text = hostingOpenCode == "" ? "Ready. Choose Open Lobby to announce, or Close Lobby to close an existing announcement for this code." : "Lobby open in Discord. Choose Close Lobby when you finish hosting.";
    }
    void RefreshHostingLobbyButtons()
    {
        bool valid = false;
        try { HostingAnnouncements.Code(HostingLobbyCode.Text); HostingAnnouncements.LobbyName(HostingLobbyName.Text); _ = HostingGamemode; _ = HostingAnnouncements.MapName(HostingLobbyMap.Text); valid = true; } catch { }
        bool available = valid && hostingActor != null && !string.IsNullOrWhiteSpace(HostingEndpoint) && !hostingLobbyBusy;
        HostingOpenLobbyButton.IsEnabled = available && hostingOpenCode == "";
        HostingCloseLobbyButton.IsEnabled = available;
        HostingUpdateGamemodeButton.Visibility = hostingOpenCode.Length > 0 && hostingAnnouncementId != null ? Visibility.Visible : Visibility.Collapsed;
        HostingUpdateGamemodeButton.IsEnabled = available && hostingOpenCode.Length > 0 && hostingAnnouncementId != null;
        HostingGamemodeSource.IsEnabled = !hostingLobbyBusy;
        HostingCustomGamemode.IsReadOnly = HostingLobbyMap.IsReadOnly = hostingLobbyBusy;
        HostingCustomGamemodePanel.Visibility = HostingGamemodeSourceValue == "custom" ? Visibility.Visible : Visibility.Collapsed;
        HostingSpinButton.IsEnabled = !hostingLobbyBusy;
        HostingLobbyCode.IsReadOnly = HostingLobbyName.IsReadOnly = hostingLobbyBusy || hostingOpenCode != "";
    }
    async void HostingOpenLobby(object sender, RoutedEventArgs e) => await SendHostingAnnouncement("open");
    async void HostingCloseLobby(object sender, RoutedEventArgs e) => await SendHostingAnnouncement("close");
    async void HostingUpdateGamemode(object sender, RoutedEventArgs e) => await SendHostingAnnouncement("update");
    async Task SendHostingAnnouncement(string action)
    {
        if (hostingLobbyBusy || action == "open" && hostingOpenCode != "" || action == "update" && (hostingOpenCode == "" || hostingAnnouncementId == null)) return;
        int generation = hostingGeneration;
        string code, lobbyName, gamemode, map;
        try { code = HostingAnnouncements.Code(HostingLobbyCode.Text); lobbyName = HostingAnnouncements.LobbyName(HostingLobbyName.Text); gamemode = action == "close" ? "" : HostingGamemode; map = action == "close" ? "" : HostingAnnouncements.MapName(HostingLobbyMap.Text); } catch { return; }
        var session = Session(LibraryRoot).Load();
        string? actor = (string?)session?["user"]?["id"], token = (string?)session?["token"];
        string endpoint = HostingEndpoint;
        if (actor == null || actor != hostingActor || token == null || string.IsNullOrWhiteSpace(endpoint)) { RefreshHostingAccount(); return; }
        string key = actor + ":" + action + ":" + code + (action == "open" ? ":" + new JsonArray(lobbyName, gamemode, map).ToJsonString() : action == "update" ? ":" + hostingAnnouncementId + ":" + new JsonArray(gamemode, map).ToJsonString() : "");
        if (action == "update") foreach (string old in hostingRequestIds.Keys.Where(k => k.StartsWith(actor + ":update:" + code + ":", StringComparison.Ordinal) && k != key).ToArray()) hostingRequestIds.Remove(old);
        if (!hostingRequestIds.TryGetValue(key, out string? requestId)) hostingRequestIds[key] = requestId = Guid.NewGuid().ToString("N");
        using var cancel = new CancellationTokenSource();
        hostingAnnouncementRequest = cancel; hostingLobbyBusy = true; RefreshHostingLobbyButtons();
        HostingAnnouncementStatus.Text = action == "open" ? "Opening lobby…" : action == "update" ? "Updating lobby details…" : "Closing lobby…";
        bool Current() => generation == hostingGeneration && actor == hostingActor && endpoint == HostingEndpoint && token == (string?)Session(LibraryRoot).Load()?["token"];
        try
        {
            var result = await HostingAnnouncements.Announce(endpoint, token, code, requestId, cancel.Token, hostingTransport, lobbyName, action, gamemode, hostingAnnouncementId, map);
            if (!Current()) return;
            bool closed = (string?)result["status"] == "closed" || (string?)result["lobbyStatus"] == "closed";
            hostingOpenCode = closed ? "" : code;
            hostingAnnouncementId = closed ? null : (string?)result["announcementId"] ?? hostingAnnouncementId;
            if (closed)
            {
                foreach (string old in hostingRequestIds.Keys.Where(k => k.StartsWith(actor + ":open:" + code + ":", StringComparison.Ordinal)).ToArray()) hostingRequestIds.Remove(old);
                HostingAnnouncementStatus.Text = "Lobby closed in Discord.";
            }
            else
            {
                hostingRequestIds.Remove(actor + ":close:" + code);
                if (action == "update") hostingRequestIds.Remove(key);
                HostingAnnouncementStatus.Text = action == "update" ? "Lobby details updated in Discord." : "Lobby open in Discord. Choose Close Lobby when you finish hosting.";
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (HostingApiException error) when (error.Status == HttpStatusCode.Unauthorized)
        {
            if (Current()) { Session(LibraryRoot).Clear(); RefreshSharingAccount(); HostingAnnouncementStatus.Text = "Sign in with Discord again, then enter the lobby code."; }
        }
        catch (Exception error)
        {
            if (!Current()) return;
            int retry = error is HostingApiException api ? api.RetryAfter : error is HttpRequestException or OperationCanceledException ? 15 : 0;
            HostingAnnouncementStatus.Text = error is HostingApiException ? error.Message : "Lobby request not confirmed. Check the Discord channel, then retry the same button.";
            if (action is "close" or "update" && error is HostingApiException { Status: HttpStatusCode.NotFound })
            {
                hostingOpenCode = ""; hostingAnnouncementId = null;
                foreach (string old in hostingRequestIds.Keys.Where(k => k.StartsWith(actor + ":open:" + code + ":", StringComparison.Ordinal)).ToArray()) hostingRequestIds.Remove(old);
                HostingAnnouncementStatus.Text += " Check Discord; you can edit the lobby details.";
            }
            if (retry > 0) HostingAnnouncementStatus.Text += $" Wait {retry} seconds, then retry the same button.";
        }
        finally { if (ReferenceEquals(hostingAnnouncementRequest, cancel)) { hostingAnnouncementRequest = null; hostingLobbyBusy = false; RefreshHostingLobbyButtons(); } }
    }
    void HostingPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!hostingReady || renderingHosting || HostingPreset.SelectedItem == null) return;
        hostingSpinner.Reset(); hostingResult = null; RenderHostingRotation();
        var prefs = Preferences(); prefs["hostingPreset"] = (string)HostingPreset.SelectedItem; SGL.Core.Json.Atomic(Path.Combine(dataRoot, "settings.json"), prefs.ToJsonString());
    }
    void RenderHostingRotation()
    {
        renderingHosting = true;
        var rotation = CurrentHostingRotation;
        HostingModesInput.Text = string.Join(Environment.NewLine, rotation.Gamemodes);
        HostingMapLists.ItemsSource = rotation.MapLists.Keys.ToArray(); HostingMapLists.SelectedIndex = 0;
        HostingAssignedMode.ItemsSource = rotation.Gamemodes; HostingAssignedMode.SelectedIndex = 0;
        HostingAssignedLists.ItemsSource = rotation.MapLists.Keys.ToArray();
        renderingHosting = false; LoadHostingMapList(); LoadHostingLinks();
        HostingSpinMode.Text = hostingResult?.Gamemode ?? "Ready to spin"; HostingSpinMap.Text = hostingResult?.Map ?? "—";
        CopyHostingModeButton.IsEnabled = CopyHostingMapButton.IsEnabled = hostingResult != null;
    }
    void SaveHostingRotation(string message)
    {
        string json = HostingRotation.Write(hostingRotations);
        if (File.Exists(HostingRotationsPath)) File.Copy(HostingRotationsPath, HostingRotationsPath + ".previous", true);
        SGL.Core.Json.Atomic(HostingRotationsPath, json);
        var prefs = Preferences(); prefs["hostingPreset"] = (string)HostingPreset.SelectedItem; SGL.Core.Json.Atomic(Path.Combine(dataRoot, "settings.json"), prefs.ToJsonString());
        hostingSpinner.Reset(); hostingResult = null; RenderHostingRotation(); HostingSpinStatus.Text = message + " Rotation history reset.";
    }
    void HostingEdit(Action action)
    {
        var original = hostingRotations.ToDictionary(p => p.Key, p => p.Value.Copy());
        string selected = (string)HostingPreset.SelectedItem;
        string modes = HostingModesInput.Text, maps = HostingMapsInput.Text, listName = HostingMapListName.Text, presetName = HostingPresetName.Text;
        try { action(); }
        catch (Exception error)
        {
            hostingRotations = original; renderingHosting = true;
            HostingPreset.ItemsSource = hostingRotations.Keys.ToArray(); HostingPreset.SelectedItem = selected;
            renderingHosting = false; RenderHostingRotation();
            HostingModesInput.Text = modes; HostingMapsInput.Text = maps; HostingMapListName.Text = listName; HostingPresetName.Text = presetName;
            HostingSpinStatus.Text = error.Message;
        }
    }
    async void HostingSpinClick(object sender, RoutedEventArgs e) => await SpinHosting();
    async Task SpinHosting()
    {
        if (hostingLobbyBusy) return;
        try { hostingResult = hostingSpinner.Spin(CurrentHostingRotation); renderingHosting = true; HostingLobbyMap.Text = hostingResult.Map; renderingHosting = false; HostingSpinMode.Text = hostingResult.Gamemode; HostingSpinMap.Text = hostingResult.Map; CopyHostingModeButton.IsEnabled = CopyHostingMapButton.IsEnabled = true; HostingSpinStatus.Text = "Using " + string.Join(" + ", CurrentHostingRotation.Assignments[hostingResult.Gamemode]) + ". Spin again for another matchup.";
            if (hostingOpenCode.Length > 0 && hostingAnnouncementId != null) await SendHostingAnnouncement("update");
        }
        catch (Exception error) { HostingSpinStatus.Text = error.Message; }
    }
    void CopyHostingMode(object sender, RoutedEventArgs e) => Run(() => { if (hostingResult != null) Clipboard.SetText(HostingRotation.CopyModeName(hostingResult.Gamemode)); });
    void CopyHostingMap(object sender, RoutedEventArgs e) => Run(() => { if (hostingResult != null) Clipboard.SetText(hostingResult.Map); });
    void CopyHostingPreset(object sender, RoutedEventArgs e) => HostingEdit(() =>
    {
        string name = HostingRotation.Name(HostingPresetName.Text);
        if (hostingRotations.Keys.Any(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)) || hostingRotations.Count >= 64) throw new InvalidOperationException("Choose a unique preset name; up to 64 presets are supported.");
        hostingRotations.Add(name, CurrentHostingRotation.Copy()); renderingHosting = true; HostingPreset.ItemsSource = hostingRotations.Keys.ToArray(); HostingPreset.SelectedItem = name; renderingHosting = false; HostingPresetName.Clear(); SaveHostingRotation("Preset copied.");
    });
    void DeleteHostingPreset(object sender, RoutedEventArgs e) => HostingEdit(() =>
    {
        if (hostingRotations.Count < 2) throw new InvalidOperationException("Keep at least one preset.");
        if (!Confirm("Delete rotation preset " + HostingPreset.SelectedItem + "?")) return;
        hostingRotations.Remove((string)HostingPreset.SelectedItem); renderingHosting = true; HostingPreset.ItemsSource = hostingRotations.Keys.ToArray(); HostingPreset.SelectedIndex = 0; renderingHosting = false; SaveHostingRotation("Preset deleted.");
    });
    void RestoreHostingPreset(object sender, RoutedEventArgs e) => HostingEdit(() =>
    {
        string name = (string)HostingPreset.SelectedItem;
        if (!HostingRotation.Defaults().TryGetValue(name, out var rotation)) throw new InvalidOperationException("Restore is available for Original and Quick Play.");
        if (!Confirm("Restore built-in rotation " + name + "?")) return;
        hostingRotations[name] = rotation; SaveHostingRotation("Built-in restored.");
    });
    void SaveHostingModes(object sender, RoutedEventArgs e) => HostingEdit(() =>
    {
        var modes = HostingRotation.Lines(HostingModesInput.Text); if (modes.Count == 0) throw new InvalidOperationException("Keep at least one gamemode.");
        CurrentHostingRotation.Gamemodes = modes;
        foreach (string key in CurrentHostingRotation.Assignments.Keys.Except(modes).ToArray()) CurrentHostingRotation.Assignments.Remove(key);
        SaveHostingRotation("Gamemodes saved.");
    });
    void AddInstalledHostingModes(object sender, RoutedEventArgs e) => HostingEdit(() =>
    {
        CurrentHostingRotation.Gamemodes = CurrentHostingRotation.Gamemodes.Concat(snapshot?.Modes.Select(m => m.Name) ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); SaveHostingRotation("Installed gamemodes added. Link their map lists below.");
    });
    void LoadHostingMapList()
    {
        if (HostingMapLists.SelectedItem is not string name) return;
        HostingMapListName.Text = name; HostingMapsInput.Text = string.Join(Environment.NewLine, CurrentHostingRotation.MapLists[name]);
    }
    void HostingMapListSelected(object sender, SelectionChangedEventArgs e) { if (hostingReady && !renderingHosting) LoadHostingMapList(); }
    void NewHostingMapList(object sender, RoutedEventArgs e) { HostingMapLists.SelectedItem = null; HostingMapListName.Clear(); HostingMapsInput.Clear(); }
    void UseInstalledHostingMaps(object sender, RoutedEventArgs e) { HostingMapListName.Text = "Installed maps"; HostingMapsInput.Text = string.Join(Environment.NewLine, snapshot?.Maps.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase) ?? []); }
    void SaveHostingMapList(object sender, RoutedEventArgs e) => HostingEdit(() =>
    {
        string name = HostingRotation.Name(HostingMapListName.Text); var maps = HostingRotation.Lines(HostingMapsInput.Text);
        if (maps.Count == 0) throw new InvalidOperationException("Add at least one map.");
        CurrentHostingRotation.MapLists[name] = maps; SaveHostingRotation("Map list saved.");
    });
    void DeleteHostingMapList(object sender, RoutedEventArgs e) => HostingEdit(() =>
    {
        if (HostingMapLists.SelectedItem is not string name || CurrentHostingRotation.MapLists.Count <= 1) throw new InvalidOperationException("Select a map list and keep at least one list.");
        if (!Confirm("Delete map list " + name + " and its links?")) return;
        CurrentHostingRotation.MapLists.Remove(name); foreach (var links in CurrentHostingRotation.Assignments.Values) links.Remove(name); SaveHostingRotation("Map list deleted.");
    });
    void LoadHostingLinks()
    {
        HostingAssignedLists.UnselectAll(); if (HostingAssignedMode.SelectedItem is string mode) foreach (string list in CurrentHostingRotation.Assignments.GetValueOrDefault(mode, [])) HostingAssignedLists.SelectedItems.Add(list);
    }
    void HostingAssignedModeChanged(object sender, SelectionChangedEventArgs e) { if (hostingReady && !renderingHosting) LoadHostingLinks(); }
    void SaveHostingLinks(object sender, RoutedEventArgs e) => HostingEdit(() =>
    {
        if (HostingAssignedMode.SelectedItem is not string mode) throw new InvalidOperationException("Select a gamemode.");
        CurrentHostingRotation.Assignments[mode] = HostingAssignedLists.SelectedItems.Cast<string>().ToList(); SaveHostingRotation("Map-list links saved.");
    });
}
