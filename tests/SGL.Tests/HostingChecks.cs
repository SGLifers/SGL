using System.Net;
using System.Text.Json.Nodes;
using SGL.Core;

static class HostingChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        void Reject(Action action, string message) { bool failed = false; try { action(); } catch { failed = true; } check(failed, message); }
        var presets = HostingRotation.Defaults();
        check(presets.Count == 2 && presets.ContainsKey("Original") && presets.ContainsKey("Quick Play"), "SpinGate presets missing");
        check(presets["Original"].Gamemodes.Count == 8 && presets["Quick Play"].Gamemodes.Count == 10, "SpinGate gamemode pools changed");
        check(presets["Original"].MapsFor("Takedown").All(m => m.StartsWith("Simulation ")), "Takedown uses arena maps");
        check(presets["Quick Play"].MapsFor("Classic Takedown").Length == 6, "Quick Play Takedown mapping changed");
        check(presets["Original"].MapsFor("Big Head Snipers").Contains("!Jump Party Sick"), "Original extra map list missing");
        var clone = presets["Original"].Copy(); clone.MapLists["Arena maps"].Clear();
        check(presets["Original"].MapLists["Arena maps"].Count == 18, "Preset copy mutated original");
        check(HostingRotation.Read(HostingRotation.Write(presets))["Original"].Gamemodes.SequenceEqual(presets["Original"].Gamemodes), "Rotation persistence changed modes");
        check(HostingRotation.Lines("TDM\r\n\n TDM\nKOTH").SequenceEqual(new[] { "TDM", "KOTH" }), "Rotation text did not normalize unique lines");
        Reject(() => HostingRotation.Read("{}"), "Empty rotation state accepted");
        Reject(() => HostingRotation.Read("{\"bad\":null}"), "Null preset accepted");
        var invalid = presets["Original"].Copy(); invalid.Assignments["missing"] = ["Arena maps"];
        Reject(invalid.Validate, "Dangling rotation assignment accepted");
        check(HostingRotation.CopyModeName("Team Deathmatch") == "Team Deathmatch (1 Round)" && HostingRotation.CopyModeName("Takedown") == "Takedown (OG)", "Game menu copy names changed");
        var spinner = new HostingSpinner(n => 0);
        var recent = new Queue<string>(); var lastMaps = new Dictionary<string, int>();
        var original = presets["Original"];
        for (int turn = 1; turn <= 200; turn++)
        {
            var result = spinner.Spin(original);
            check(!recent.Contains(result.Gamemode), "Spin repeated a recent gamemode");
            recent.Enqueue(result.Gamemode); while (recent.Count > 4) recent.Dequeue();
            check(original.MapsFor(result.Gamemode).Contains(result.Map), "Spin returned an incompatible map");
            lastMaps[result.Map] = turn;
        }
        var narrow = new HostingRotation { Gamemodes = ["A", "B", "C"], MapLists = new() { ["big"] = ["X", "Y", "Z"], ["tiny"] = ["X"] }, Assignments = new() { ["A"] = ["big"], ["B"] = ["tiny"], ["C"] = ["big"] } };
        spinner.Reset();
        for (int i = 0; i < 30; i++) check(spinner.Spin(narrow).Map != null, "Overlapping map cooldown exhausted its pool");
        var none = original.Copy(); none.Assignments.Clear(); Reject(() => spinner.Spin(none), "Unlinked rotation produced a result");
        check(HostingAnnouncements.Code(" ab12cd ") == "AB12CD", "Lobby normalization failed");
        foreach (string code in new[] { "abc", "@everyone", "ABC`12", "A\nB123", "X" + new string('A', 32) }) Reject(() => HostingAnnouncements.Code(code), "Unsafe lobby code accepted");
        foreach (string url in new[] { "http://bot.example", "https://user:secret@bot.example", "https://bot.example/?secret=value", "https://bot.example:444" }) Reject(() => HostingAnnouncements.Endpoint(url), "Unsafe hosting endpoint accepted");
        var handler = new HostHandler();
        var response = await HostingAnnouncements.Announce("https://bot.example/api/", new string('t', 43), "abc123", new string('a', 32), handler: handler);
        check((string?)response["status"] == "sent" && handler.Path == "/api/announcements", "Announcement route contract changed");
        check(handler.Body!.AsObject().Count == 2 && (string?)handler.Body["code"] == "ABC123" && handler.Authorization == "Bearer " + new string('t', 43), "Client sent spoofable identity or omitted authentication");
        handler.Status = HttpStatusCode.TooManyRequests; handler.Reply = "{\"error\":\"private token value\",\"retryAfter\":60}";
        try { await HostingAnnouncements.Announce("https://bot.example", new string('t', 43), "ABC123", new string('a', 32), handler: handler); check(false, "Rate limit was accepted"); }
        catch (HostingApiException error) { check(error.RetryAfter == 60 && !error.Message.Contains("private token"), "Upstream text leaked or retry delay lost"); }
    }
    sealed class HostHandler : HttpMessageHandler
    {
        public string? Path, Authorization; public JsonNode? Body;
        public HttpStatusCode Status = HttpStatusCode.OK; public string Reply = "{\"status\":\"sent\",\"messageId\":\"123456789012345678\"}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Path = request.RequestUri!.AbsolutePath; Authorization = request.Headers.Authorization?.ToString(); Body = Json.Read(await request.Content!.ReadAsStringAsync(cancel));
            return new(Status) { Content = new StringContent(Reply) };
        }
    }
}
