using System.Text;
using System.Text.Json.Nodes;
using SGL.Core;

static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
static string Line(int second, string value) => $"[2026.11.26-12.00.{second:00}:000][  1]{value}\n";
static string Start(int second = 0, string mode = "Domination", string role = "SERVER") => Line(second, $"LogGF1047Experience: Starting experience load ({role}): Default__EXP_{mode}_C");
static string Outcome(int second = 3) => Line(second, "LogGF1047Experience: SetExperienceOutcome called:") + "Tag: Experience.Outcome.Winner\nReason: TimeLimit\nWinners:\n- Team_0\nScores:\nTeam_0 = 7\nRénée = 1200\nTeam_1 = 4\nOther = 900\n";
static string MatchText() => Line(0, "LogLoad: LoadMap: /Game/Maps/Abyss?listen") + Start(1) + Line(2, "LogGF1047Medal: Awarding MD_Defender to Rénée") + Outcome() + Start(4, "PostGame_Generic");
static List<JsonObject> Parse(string text)
{
    var parser = new StatisticsLog("collector", "generation");
    var results = new List<JsonObject>();
    long offset = 0;
    foreach (string line in text.Split('\n'))
    {
        results.AddRange(parser.Line(line, offset));
        offset += Encoding.UTF8.GetByteCount(line) + 1;
    }
    return results;
}
var result = Parse(MatchText()).Single();
Check((string?)result["completion"] == "complete", "natural complete match");
Check((string?)result["mode"]?["family"] == "domination", "mode from experience");
Check(!result.ToJsonString().Contains("kills"), "raw score never becomes kills");
Check((string?)result["clockStatus"] == "unverified" && result["endedAt"] == null, "unverified log clock remains explicit");
Check(result["medals"]!.AsArray().Count == 1 && result["medals"]![0]!["participantId"] != null, "medal unambiguously linked");
Check((string?)Parse(Start(mode: "Deathmatch") + Outcome() + Start(4, "PostGame_Generic")).Single()["mode"]?["family"] == "unknown", "ambiguous deathmatch");
Check((string?)Parse(Start() + Start(4, "PostGame_Generic")).Single()["completion"] == "incomplete", "missing outcome");
Check((string?)Parse(Start() + Start(2, role: "CLIENT") + Outcome() + Start(4, "PostGame_Generic", role: "CLIENT")).Single()["completion"] == "incomplete", "host authority loss");
var multi = Parse(Start(mode: "Takedown") + Outcome() + Start(4, "Intermission_Generic") + Start(5, "Takedown") + Outcome(7) + Start(9, "PostGame_Generic"));
Check(multi.Count == 1 && multi[0]["rounds"]!.AsArray().Count == 2, "rounds remain one match");
Check((string?)Parse(Start() + Outcome() + Start(4, "Intermission_Generic") + Start(5) + Start(6, "PostGame_Generic")).Single()["completion"] == "incomplete", "manual end with earlier outcomes");
Check(Parse(Start() + Line(2, "LogChat: LogGF1047Medal: Awarding MD_Fake to Guest") + Outcome() + Start(4, "PostGame_Generic")).Single()["medals"]!.AsArray().Count == 0, "quoted chat not event");
string duplicate = MatchText().Replace("Other = 900", "Rénée = 900");
var dup = Parse(duplicate).Single();
Check(dup["medals"]![0]!["participantId"] == null, "duplicate names unlinked");
var roster = Parse(Line(0, "LogNet: Join succeeded: Departed") + MatchText() + Start(5) + Outcome(7) + Start(9, "PostGame_Generic"));
Check(roster.Count == 2 && !roster[1]["participants"]!.AsArray().Any(p => (string?)p!["displayName"] == "Departed"), "prior match join does not create later participation");
string directory = Path.Combine(Path.GetTempPath(), "sgl-stats-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    byte[] bytes = Encoding.UTF8.GetBytes(MatchText());
    string database = Path.Combine(directory, "journal.sqlite");
    using (var journal = new StatisticsJournal(database, "collector", "generation"))
        for (int i = 0; i < bytes.Length; i++)
            journal.Ingest([bytes[i]], i);
    string sealedBody;
    using (var journal = StatisticsJournal.OpenExisting(database))
    {
        var queued = journal.Next(long.MaxValue)!.Value;
        sealedBody = queued.Body;
        Check((long)journal.Checkpoint()["offset"]! == bytes.Length, "cursor survives restart");
        Check(queued.Body == result.ToJsonString(), "fragmented UTF8 matches whole parser");
        bool refused = false;
        try
        {
            journal.Ingest(bytes, 0);
        }
        catch (InvalidDataException) { refused = true; }
        Check(refused, "overlap rejected");
        journal.Delivery(queued.Id, "received", 0, 1);
        Check(journal.Next(1)!.Value.Body == sealedBody, "retry exact immutable bytes");
        journal.Delivery(queued.Id, "published", 0, 2);
        Check(journal.Next(long.MaxValue) == null, "published result not resent");
    }
    string invalid = Path.Combine(directory, "invalid.sqlite");
    using (var j = new StatisticsJournal(invalid, "c", "g"))
    {
        bool refused = false;
        try
        {
            j.Ingest([255, 10], 0);
        }
        catch (DecoderFallbackException) { refused = true; }
        Check(refused && (long)j.Checkpoint()["offset"]! == 0, "invalid UTF8 rolls back checkpoint");
    }
    string deliveryDirectory = Path.Combine(directory, "delivery");
    Directory.CreateDirectory(deliveryDirectory);
    string deliveryDb = Path.Combine(deliveryDirectory, "queue.sqlite");
    using (var j = new StatisticsJournal(deliveryDb, "collector", "delivery"))
        j.Ingest(bytes, 0);
    string oid, bodyText;
    using (var j = StatisticsJournal.OpenExisting(deliveryDb))
    {
        var next = j.Next(long.MaxValue)!.Value;
        oid = next.Id;
        bodyText = next.Body;
    }
    var fake = new StatsTransport();
    await using (var delivery = new StatisticsCollection(directory, "actor", "token", "https://stats.example.test", Path.Combine(directory, "unused.log"), _ => { }, fake))
    {
        fake.Status = System.Net.HttpStatusCode.ServiceUnavailable;
        await delivery.Upload(deliveryDirectory, default);
        Check(fake.Bodies.Single() == bodyText && fake.Keys.Single() == oid, "upload uses sealed body and idempotency key");
        using (var j = StatisticsJournal.OpenExisting(deliveryDb))
        {
            Check(j.Next(long.MaxValue)!.Value.State == "pending", "server failure retains pending result");
            j.Delivery(oid, "pending", 0, 1);
        }
        fake.Status = System.Net.HttpStatusCode.Accepted;
        fake.ReceiptId = oid;
        fake.ReceiptStatus = "received";
        await delivery.Upload(deliveryDirectory, default);
        using (var j = StatisticsJournal.OpenExisting(deliveryDb))
        {
            Check(j.Next(long.MaxValue)!.Value.State == "received", "202 waits for publication");
            j.Delivery(oid, "received", 0, 2);
        }
        fake.ReceiptStatus = "published";
        await delivery.Upload(deliveryDirectory, default);
        using (var j = StatisticsJournal.OpenExisting(deliveryDb))
            Check(j.Next(long.MaxValue) == null && fake.Methods.Last() == "GET", "receipt poll finalizes published result");
        using (var j = StatisticsJournal.OpenExisting(deliveryDb))
            j.Delivery(oid, "pending", 0, 3);
        fake.Status = System.Net.HttpStatusCode.Unauthorized;
        await delivery.Upload(deliveryDirectory, default);
        using (var j = StatisticsJournal.OpenExisting(deliveryDb))
        {
            Check(j.Next(long.MaxValue) == null, "authorization failure blocks automatic retry");
            j.ResumeAuthorization();
            Check(j.Next(1)!.Value.Body == bodyText, "new authorized session retains exact body");
        }
        fake.Status = System.Net.HttpStatusCode.Accepted;
        fake.ReceiptId = "wrong";
        await delivery.Upload(deliveryDirectory, default);
        using (var j = StatisticsJournal.OpenExisting(deliveryDb))
        {
            Check(j.Next(long.MaxValue)!.Value.State == "pending", "mismatched receipt never acknowledges observation");
            j.Delivery(oid, "pending", 0, 5);
        }
        fake.Status = System.Net.HttpStatusCode.Redirect;
        await delivery.Upload(deliveryDirectory, default);
        using (var j = StatisticsJournal.OpenExisting(deliveryDb))
            Check(j.Next(long.MaxValue) == null, "redirect is rejected without a second request");
    }
    if (args.Length > 0)
        File.WriteAllText(args[0], new JsonArray(result.DeepClone()).ToJsonString());
}
finally { Directory.Delete(directory, true); }

sealed class StatsTransport : HttpMessageHandler
{
    public System.Net.HttpStatusCode Status = System.Net.HttpStatusCode.Accepted;
    public string ReceiptId = "", ReceiptStatus = "received";
    public List<string> Bodies = new(), Keys = new(), Methods = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Methods.Add(request.Method.Method);
        Keys.Add(request.Headers.GetValues("Idempotency-Key").Single());
        if (request.Content != null)
            Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
        return new HttpResponseMessage(Status) { Content = new StringContent(new JsonObject { ["id"] = ReceiptId, ["status"] = ReceiptStatus }.ToJsonString()) };
    }
}
