using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace SGL.Core;

/// <summary>Incremental log facts only. Display names are observations, not accounts.</summary>
public sealed class StatisticsLog
{
    static readonly Regex Stamp = new(@"^\[(\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2}:\d{3,6})\]\[\s*\d+\]", RegexOptions.CultureInvariant);
    static readonly Regex Experience = new(@"^Starting experience load \((SERVER|CLIENT)\): Default__EXP_(\w+)_C$", RegexOptions.CultureInvariant);
    static readonly Dictionary<string, string> Modes = new() { ["KOTH"] = "koth", ["Domination"] = "domination", ["Takedown"] = "takedown", ["Firecracker"] = "firecracker", ["GunGame"] = "gungame", ["Hotzone"] = "hotzone", ["TeabagConfirmed"] = "teabag_confirmed", ["Splitball"] = "splitball" };
    public JsonObject State
    {
        get;
    }
    readonly string collector, generation;
    public StatisticsLog(string collector, string generation, JsonObject? state = null)
    {
        this.collector = collector;
        this.generation = generation;
        State = state ?? new JsonObject { ["authority"] = "unknown", ["authorityLost"] = false, ["names"] = new JsonArray(), ["lineNumber"] = 0L };
    }
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    JsonObject? Match => State["match"] as JsonObject;
    void AddName(string name)
    {
        if (name.Length is < 1 or > 128 || name.Any(char.IsControl))
            return;
        var names = State["names"]!.AsArray();
        if (!names.Any(n => (string?)n == name) && names.Count < 64)
            names.Add(name);
    }
    void CloseRound()
    {
        if (State["outcome"] is not JsonObject outcome || Match is not JsonObject match)
            return;
        State["outcome"] = null;
        if (outcome["outcome"] != null)
        {
            match["rounds"]!.AsArray().Add(outcome.DeepClone());
            match["roundOpen"] = false;
        }
    }
    JsonObject? Finish(string reason)
    {
        CloseRound();
        if (Match is not JsonObject match)
            return null;
        State["match"] = null;
        var rounds = match["rounds"]!.AsArray();
        bool valid = rounds.Count > 0 && !(bool)match["roundOpen"]! && rounds.All(r => (string?)r!["outcome"] is "Experience.Outcome.Winner" or "Experience.Outcome.Draw" && r["scores"]!.AsArray().Count > 0 && ((string?)r["outcome"] == "Experience.Outcome.Draw" || r["winners"]!.AsArray().Count > 0 && r["winners"]!.AsArray().All(w => r["scores"]!.AsArray().Any(s => (string?)s!["label"] == (string?)w))));
        bool complete = reason == "postgame" && valid && (string?)match["sourceRole"] == "server" && !(bool)State["authorityLost"]!;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in rounds)
        {
            var local = r!["scores"]!.AsArray().Where(s => (string?)s!["kind"] == "player").GroupBy(s => (string)s!["label"]!);
            foreach (var group in local)
                counts[group.Key] = Math.Max(counts.GetValueOrDefault(group.Key), group.Count());
        }
        foreach (var name in State["names"]!.AsArray().Select(n => (string)n!).Concat(match["medals"]!.AsArray().Select(m => (string)m!["displayName"]!)))
            counts.TryAdd(name, 1);
        var participants = new JsonArray();
        var refs = new Dictionary<string, string?>();
        foreach (var pair in counts.OrderBy(p => p.Key, StringComparer.Ordinal).Take(64))
        {
            for (int i = 0; i < pair.Value; i++)
            {
                string id = "p" + Hash(pair.Key + ":" + i)[..24];
                participants.Add(new JsonObject { ["id"] = id, ["displayName"] = pair.Key, ["isBot"] = null });
                refs[pair.Key] = pair.Value == 1 ? id : null;
            }
        }
        State["names"] = new JsonArray();
        var medals = match["medals"]!.AsArray();
        foreach (var medal in medals)
            medal!["participantId"] = refs.GetValueOrDefault((string)medal["displayName"]!);
        var key = new JsonObject { ["collectorId"] = collector, ["sourceGenerationId"] = generation, ["matchStartOffset"] = (long)match["startOffset"]! };
        var modes = match["modes"]!.AsArray().Select(n => (string)n!).Distinct().ToArray();
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["observationId"] = Hash(collector + ":" + generation + ":" + (long)match["startOffset"]! + ":log:1"),
            ["matchKey"] = key,
            ["source"] = "game_log",
            ["sourceRevision"] = 1,
            ["startedAt"] = null,
            ["endedAt"] = null,
            ["clockStatus"] = "unverified",
            ["sourceTimestampRaw"] = (string?)State["lastStamp"] ?? "unknown",
            ["completion"] = complete ? "complete" : "incomplete",
            ["sourceRole"] = (string?)match["sourceRole"] ?? "unknown",
            ["mode"] = new JsonObject { ["family"] = modes.Length == 1 ? modes[0] : "unknown", ["rulesScope"] = "unknown" },
            ["map"] = (string?)match["map"],
            ["participants"] = participants,
            ["rounds"] = rounds.DeepClone(),
            ["medals"] = medals.DeepClone(),
            ["finishReason"] = reason,
            ["authorityLost"] = (bool)State["authorityLost"]!
        };
    }
    public List<JsonObject> Interrupt(string reason = "stream_changed")
    {
        var result = Finish(reason);
        State["authorityLost"] = true;
        State["authority"] = "unknown";
        State["names"] = new JsonArray();
        return result is null ? [] : [result];
    }
    public List<JsonObject> Line(string line, long startOffset)
    {
        if (Encoding.UTF8.GetByteCount(line) > 65536)
            throw new InvalidDataException("Statistics log line exceeds its size limit.");
        State["lineNumber"] = (long)State["lineNumber"]! + 1;
        var stamp = Stamp.Match(line);
        if (State["outcome"] != null && (stamp.Success || string.IsNullOrWhiteSpace(line)))
            CloseRound();
        if (State["outcome"] is JsonObject outcome)
        {
            if (line.StartsWith("Tag: ", StringComparison.Ordinal))
                outcome["outcome"] = line[5..].Trim();
            else if (line.StartsWith("Reason: ", StringComparison.Ordinal))
                outcome["reason"] = line[8..].Trim();
            else if (line is "Winners:" or "Scores:")
                State["section"] = line[..^1];
            else if ((string?)State["section"] == "Winners" && line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
                outcome["winners"]!.AsArray().Add(line.Trim()[2..]);
            else if ((string?)State["section"] == "Scores")
            {
                var score = Regex.Match(line, @"^(.*?) = (-?\d+(?:\.\d+)?)$");
                if (score.Success && double.TryParse(score.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value))
                    outcome["scores"]!.AsArray().Add(new JsonObject { ["label"] = score.Groups[1].Value, ["kind"] = Regex.IsMatch(score.Groups[1].Value, @"^Team_\d+$") ? "team" : "player", ["value"] = value });
            }
            if (outcome["scores"]!.AsArray().Count > 128 || outcome["winners"]!.AsArray().Count > 64)
                throw new InvalidDataException("Statistics outcome exceeds its roster limit.");
            return [];
        }
        if (!stamp.Success)
            return [];
        State["lastStamp"] = stamp.Groups[1].Value;
        string rest = line[stamp.Length..];
        int colon = rest.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0)
            return [];
        string category = rest[..colon], message = rest[(colon + 2)..];
        var results = new List<JsonObject>();
        if (category == "LogLoad" && message.StartsWith("LoadMap: ", StringComparison.Ordinal))
        {
            if (Finish("map_changed") is JsonObject ended)
                results.Add(ended);
            State["map"] = message[9..].Split('?')[0];
            State["names"] = new JsonArray();
        }
        if (category == "LogNet" && message.StartsWith("Join succeeded: ", StringComparison.Ordinal))
            AddName(message[16..].Trim());
        var exp = category == "LogGF1047Experience" ? Experience.Match(message) : System.Text.RegularExpressions.Match.Empty;
        if (exp.Success)
        {
            string role = exp.Groups[1].Value.ToLowerInvariant(), experience = exp.Groups[2].Value;
            if ((string?)State["authority"] == "server" && role == "client")
                State["authorityLost"] = true;
            State["authority"] = role;
            if (experience is "PostGame_Generic" or "PreGame_Generic")
            {
                if (Finish(experience == "PostGame_Generic" ? "postgame" : "pregame") is JsonObject ended)
                    results.Add(ended);
            }
            else if (experience != "Intermission_Generic")
            {
                if (Match is null)
                    State["match"] = new JsonObject { ["startOffset"] = startOffset, ["sourceRole"] = role, ["map"] = (string?)State["map"], ["rounds"] = new JsonArray(), ["medals"] = new JsonArray(), ["modes"] = new JsonArray() };
                Match!["roundOpen"] = true;
                if (Modes.TryGetValue(experience, out string? mode) && !Match["modes"]!.AsArray().Any(n => (string?)n == mode))
                    Match["modes"]!.AsArray().Add(mode);
            }
        }
        if (Match is JsonObject active)
        {
            if (category == "LogGF1047Experience" && message == "SetExperienceOutcome called:")
            {
                if (active["rounds"]!.AsArray().Count >= 100)
                    throw new InvalidDataException("Statistics round limit exceeded.");
                State["outcome"] = new JsonObject { ["ordinal"] = active["rounds"]!.AsArray().Count + 1, ["winners"] = new JsonArray(), ["scores"] = new JsonArray() };
                State["section"] = null;
            }
            else if (category == "LogGF1047Medal" && Regex.Match(message, @"^Awarding (\S+) to (.+)$") is { Success: true } medal)
            {
                if (active["medals"]!.AsArray().Count >= 20000)
                    throw new InvalidDataException("Statistics medal limit exceeded.");
                active["medals"]!.AsArray().Add(new JsonObject { ["position"] = startOffset, ["code"] = medal.Groups[1].Value, ["displayName"] = medal.Groups[2].Value });
            }
        }
        if (category == "LogExit" && message is "Preparing to exit" or "Preparing to exit." or "Exiting.")
            results.AddRange(Interrupt("process_exit"));
        return results;
    }
}
