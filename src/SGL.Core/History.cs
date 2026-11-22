using LibGit2Sharp;
using System.Text.Json.Nodes;
namespace SGL.Core;

public sealed record Revision(string Id, string Message, DateTimeOffset Time)
{
    public override string ToString() => $"{Time.LocalDateTime:g}  {Message}  ({Id[..7]})";
}
public sealed class History : IDisposable
{
    readonly Repository repository; readonly string directory; readonly FileStream gate;
    public History(string root)
    {
        directory = Path.GetFullPath(root);
        Directory.CreateDirectory(directory);
        gate = new FileStream(Path.Combine(directory, "history.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (!Repository.IsValid(directory))
                Repository.Init(directory);
            repository = new Repository(directory);
            repository.Config.Set("core.autocrlf", false);
        }
        catch { gate.Dispose(); throw; }
    }
    public Revision[] Revisions() => repository.Commits.Take(1000).Select(c => new Revision(c.Sha, c.MessageShort, c.Committer.When)).ToArray();
    public string? Capture(GameSnapshot snapshot, string message)
    {
        // A single deterministic document makes startup snapshots atomic at Git commit level.
        var modes = new JsonObject();
        foreach (var mode in snapshot.Modes)
            modes[mode.Id] = mode.Payload.DeepClone();
        string text = Json.Canonical(modes), path = Path.Combine(directory, "modes.json");
        if (File.Exists(path) && File.ReadAllText(path) == text && (!repository.Info.IsHeadUnborn) && !repository.RetrieveStatus(new StatusOptions { IncludeUntracked = false }).IsDirty)
            return null;
        Json.Atomic(path, text);
        Commands.Stage(repository, "modes.json");
        var signature = new Signature("SGL", "local@sgl.invalid", DateTimeOffset.Now);
        return repository.Commit(message, signature, signature).Sha;
    }
    public JsonObject Snapshot(string revision)
    {
        var commit = repository.Lookup<Commit>(revision) ?? throw new InvalidDataException("Revision not found.");
        var entry = commit.Tree["modes.json"] ?? throw new InvalidDataException("Revision has no mode snapshot.");
        return (JsonObject)Json.Read(((Blob)entry.Target).GetContentText());
    }
    public string Compare(string before, string after)
    {
        var a = Snapshot(before);
        var b = Snapshot(after);
        var sections = new List<string>();
        foreach (string id in a.Select(p => p.Key).Union(b.Select(p => p.Key)).Order())
        {
            if (JsonNode.DeepEquals(a[id], b[id]))
                continue;
            var p = (JsonObject)(b[id] ?? a[id])!;
            string title = Configuration.Name(p) + " [" + id[..Math.Min(8, id.Length)] + "]";
            sections.Add(title + Environment.NewLine + (a[id] == null ? "Added configuration." : b[id] == null ? "Removed configuration." : Configuration.Diff(a[id], b[id])));
        }
        return sections.Count == 0 ? "No configuration changes." : string.Join(Environment.NewLine + Environment.NewLine, sections);
    }
    public JsonObject Mode(string revision, string id) => Snapshot(revision)[id] is JsonObject mode ? Configuration.Clone(mode) : throw new InvalidDataException("This mode did not exist in that revision.");
    public void Dispose()
    {
        repository.Dispose();
        gate.Dispose();
    }
}
