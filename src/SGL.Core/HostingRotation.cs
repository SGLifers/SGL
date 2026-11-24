using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace SGL.Core;

public sealed class HostingRotation
{
    public List<string> Gamemodes { get; set; } = [];
    public Dictionary<string, List<string>> MapLists { get; set; } = [];
    public Dictionary<string, List<string>> Assignments { get; set; } = [];
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public HostingRotation Copy() => JsonSerializer.Deserialize<HostingRotation>(JsonSerializer.Serialize(this, Options), Options)!;
    public static Dictionary<string, HostingRotation> Defaults() => Read(Modes.Resource("hosting-rotations.json"));
    public static Dictionary<string, HostingRotation> Read(string json)
    {
        if (json.Length > 2_000_000) throw new InvalidDataException("Rotation settings are too large.");
        var result = JsonSerializer.Deserialize<Dictionary<string, HostingRotation>>(json, Options) ?? throw new InvalidDataException("No rotations.");
        if (result.Count is < 1 or > 64) throw new InvalidDataException("Keep between 1 and 64 rotation presets.");
        foreach (var preset in result) { Name(preset.Key); preset.Value.Validate(); }
        return result;
    }
    public static string Write(Dictionary<string, HostingRotation> presets) { Read(JsonSerializer.Serialize(presets, Options)); return JsonSerializer.Serialize(presets, Options); }
    public static string Name(string text)
    {
        string name = text.Trim();
        if (name.Length is < 1 or > 120 || name.Any(char.IsControl)) throw new InvalidDataException("Use a name between 1 and 120 characters, without control characters.");
        return name;
    }
    public static List<string> Lines(string text) => text.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).Select(Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    public void Validate()
    {
        if (Gamemodes == null || MapLists == null || Assignments == null || Gamemodes.Count is < 1 or > 100 || MapLists.Count is < 1 or > 100 || Assignments.Count > 100) throw new InvalidDataException("Invalid rotation pools.");
        foreach (var name in Gamemodes) Name(name);
        if (Gamemodes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Gamemodes.Count) throw new InvalidDataException("Duplicate gamemodes.");
        foreach (var list in MapLists)
        {
            Name(list.Key);
            if (list.Value == null || list.Value.Count is < 1 or > 500 || list.Value.Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Value.Count) throw new InvalidDataException("Map lists need 1–500 unique map names.");
            foreach (string map in list.Value) Name(map);
        }
        foreach (var link in Assignments)
            if (!Gamemodes.Contains(link.Key) || link.Value == null || link.Value.Any(k => !MapLists.ContainsKey(k))) throw new InvalidDataException("Rotation links reference missing modes or map lists.");
    }
    public string[] MapsFor(string mode) => Assignments.GetValueOrDefault(mode, []).SelectMany(list => MapLists.GetValueOrDefault(list, [])).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public static string CopyModeName(string mode) => mode switch { "Team Deathmatch" => "Team Deathmatch (1 Round)", "Takedown" => "Takedown (OG)", _ => mode };
}

public sealed record HostingSpin(string Gamemode, string Map);
public sealed class HostingSpinner(Func<int, int>? choose = null)
{
    readonly Queue<string> recentModes = [];
    readonly Dictionary<string, int> mapTurns = new(StringComparer.OrdinalIgnoreCase);
    int turn;
    int Pick(int count)
    {
        int index = (choose ?? RandomNumberGenerator.GetInt32)(count);
        return index >= 0 && index < count ? index : throw new InvalidOperationException("Invalid random selection.");
    }
    public void Reset() { recentModes.Clear(); mapTurns.Clear(); turn = 0; }
    public HostingSpin Spin(HostingRotation rotation)
    {
        rotation.Validate();
        string[] modes = rotation.Gamemodes.Where(m => rotation.MapsFor(m).Length > 0).ToArray();
        if (modes.Length == 0) throw new InvalidOperationException("Link a gamemode to at least one map list before spinning.");
        string[] eligibleModes = modes.Where(m => !recentModes.Contains(m)).ToArray();
        if (eligibleModes.Length == 0) { recentModes.Clear(); eligibleModes = modes; }
        string mode = eligibleModes[Pick(eligibleModes.Length)];
        int modeLimit = (int)Math.Ceiling((modes.Length - 1) / 2d);
        recentModes.Enqueue(mode); while (recentModes.Count > modeLimit) recentModes.Dequeue();
        string[] maps = rotation.MapsFor(mode);
        int cooldown = (int)Math.Ceiling((maps.Length - 1) / 2d);
        turn++;
        string[] eligibleMaps = maps.Where(m => !mapTurns.TryGetValue(m, out int last) || turn - last > cooldown).ToArray();
        // Differently sized overlapping pools can temporarily exhaust their cooldown.
        if (eligibleMaps.Length == 0)
        {
            int oldest = maps.Min(m => mapTurns.GetValueOrDefault(m, int.MinValue));
            eligibleMaps = maps.Where(m => mapTurns.GetValueOrDefault(m, int.MinValue) == oldest).ToArray();
        }
        string map = eligibleMaps[Pick(eligibleMaps.Length)]; mapTurns[map] = turn;
        return new(mode, map);
    }
}
