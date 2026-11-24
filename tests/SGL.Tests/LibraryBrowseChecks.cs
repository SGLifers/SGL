using System.Text.Json.Nodes;
using SGL.Core;

static class LibraryBrowseChecks
{
    public static void Run(Action<bool, string> check)
    {
        LibraryItem Item(string kind) => new(new JsonObject { ["id"] = "same-id", ["kind"] = kind, ["ownerId"] = "owner-123", ["title"] = "Sky arena", ["latestRevisionId"] = "revision", ["description"] = "Friday event", ["owner"] = new JsonObject { ["displayName"] = "Host", ["discordId"] = "123456789" } });
        var map = Item("map"); var mode = Item("mode"); var bundle = Item("bundle");
        check(LibraryBrowseEntry.MatchesKind(map, "map") && !LibraryBrowseEntry.MatchesKind(map, "mode"), "Map shown as a gamemode");
        check(LibraryBrowseEntry.MatchesKind(mode, "mode") && !LibraryBrowseEntry.MatchesKind(mode, "map"), "Gamemode shown as a map");
        check(LibraryBrowseEntry.MatchesKind(bundle, "map") && LibraryBrowseEntry.MatchesKind(bundle, "mode"), "Paired bundle lost a content section");
        var remote = new LibraryBrowseEntry(map, null, "Installed", ["TDM"]);
        var catalog = new LibraryBrowseEntry(null, new("same-id", "Sky arena", "Architect", "Race", "", null), "Available to install", ["Race"]);
        check(remote.Key != catalog.Key, "Catalog and SGL identities collided");
        check(remote.Matches("Friday", "tdm", "HOST", "sgl"), "Cloud search/tag/creator filters lost case-insensitive matching");
        check(remote.Matches("", "", "34567", "all"), "Discord creator filter failed");
        check(!remote.Matches("", "TD", "", "all"), "Tag filter stopped requiring exact labels");
        check(!remote.Matches("", "", "", "catalog"), "Cloud item passed catalog-only source filter");
        check(catalog.Matches("race", "RACE", "arch", "catalog"), "Catalog category/creator/tag search failed");
        check(!catalog.Matches("", "", "", "sgl"), "Catalog item passed SGL-only filter");
        check(remote.Label.Contains("SGL uploads") && catalog.Label.Contains("Architects catalog"), "Source attribution missing");
        string path = Path.Combine(Path.GetTempPath(), "sgl-browse-state-" + Guid.NewGuid().ToString("N"), "state.json");
        var state = new LibraryState(path);
        string scope = "https://sgl.example/#alice";
        state.RecordPublication(scope, "mode", "local-mode", "remote-mode", "revision", "content-hash");
        byte[] original = File.ReadAllBytes(path);
        var local = state.PublicationForItem(scope, "remote-mode");
        check((string?)local?["kind"] == "mode" && (string?)local?["localFileId"] == "local-mode", "Owned publication lost its local identity");
        check(state.PublicationForItem("https://sgl.example/#bob", "remote-mode") == null, "Owned publication lookup crossed Discord accounts");
        check(File.ReadAllBytes(path).SequenceEqual(original), "Publication lookup mutated tracking data");
        state.RecordInstall("https://sgl.example", [new JsonObject { ["itemId"] = "one", ["localFileId"] = "map-one", ["kind"] = "map" }]);
        state.RecordInstall("https://other.example", [new JsonObject { ["itemId"] = "two", ["localFileId"] = "map-two", ["kind"] = "map" }]);
        check(state.Installations("https://sgl.example/").Count == 1, "Installed status crossed sharing services");
    }
}
