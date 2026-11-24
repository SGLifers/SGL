using System.Text.Json.Nodes;
namespace SGL.Core;

public sealed record LibraryBrowseEntry(LibraryItem? Publication, CommunityMap? Catalog, string Status, string[] Tags)
{
    public string Key => Publication != null ? "sgl:" + Publication.Id : "catalog:" + Catalog!.Id;
    public string Kind => Publication?.Kind ?? "map";
    public string Title => Publication?.Title ?? Catalog!.Name;
    public string Creator => Publication?.OwnerName ?? Catalog!.Author;
    public string Source => Publication != null ? "SGL uploads" : "Architects catalog";
    public string Label => $"{Creator} · {Source}" + (Kind == "bundle" ? " · Map + gamemode" : "");
    public string TagSummary => string.Join(" · ", Tags);
    public JsonObject? Preview => Publication?.Preview;
    public static bool MatchesKind(LibraryItem item, string kind) => item.Kind == kind || item.Kind == "bundle";
    public bool Matches(string search, string tag, string creator, string source) =>
        (source == "all" || source == (Catalog != null ? "catalog" : "sgl"))
        && (tag.Length == 0 || Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        && (creator.Length == 0 || Creator.Contains(creator, StringComparison.OrdinalIgnoreCase)
            || Publication?.OwnerId.Contains(creator, StringComparison.OrdinalIgnoreCase) == true
            || ((string?)Publication?.Json["owner"]?["discordId"])?.Contains(creator, StringComparison.OrdinalIgnoreCase) == true)
        && (search.Length == 0 || new[] { Title, Label, Publication?.Description ?? Catalog?.Category ?? "", TagSummary }.Any(s => s.Contains(search, StringComparison.OrdinalIgnoreCase)));
}
