using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
namespace SGL.Core;

/// <summary>The parser checkpoint and immutable outbox are committed in one transaction.</summary>
public sealed class StatisticsJournal : IDisposable
{
    readonly SqliteConnection db;
    readonly string collector, generation;
    public StatisticsJournal(string path, string collector, string generation, long initialOffset = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        this.collector = collector;
        this.generation = generation;
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        db.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS metadata(id INTEGER PRIMARY KEY,data TEXT NOT NULL); CREATE TABLE IF NOT EXISTS outbox(id TEXT PRIMARY KEY,body TEXT NOT NULL,state TEXT NOT NULL DEFAULT 'pending',attempts INTEGER NOT NULL DEFAULT 0,next_attempt INTEGER NOT NULL DEFAULT 0); CREATE TRIGGER IF NOT EXISTS immutable_stats BEFORE UPDATE OF body ON outbox BEGIN SELECT RAISE(ABORT,'Statistics body is immutable'); END;");
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO metadata VALUES(1,$data)";
        cmd.Parameters.AddWithValue("$data", new JsonObject { ["collector"] = collector, ["generation"] = generation, ["offset"] = initialOffset, ["partial"] = "", ["parser"] = new StatisticsLog(collector, generation).State }.ToJsonString());
        cmd.ExecuteNonQuery();
        var state = Checkpoint();
        if ((string?)state["collector"] != collector || (string?)state["generation"] != generation)
            throw new InvalidDataException("Statistics journal belongs to a different source.");
    }
    void Execute(string text)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = text;
        cmd.ExecuteNonQuery();
    }
    public JsonObject Checkpoint(SqliteTransaction? transaction = null)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT data FROM metadata WHERE id=1";
        return JsonNode.Parse((string)cmd.ExecuteScalar()!)!.AsObject();
    }
    public void Ingest(byte[] bytes, long offset)
    {
        if (bytes.Length > 256 * 1024)
            throw new InvalidDataException("Statistics chunk is too large.");
        using var tx = db.BeginTransaction();
        var state = Checkpoint(tx);
        if ((long)state["offset"]! != offset)
            throw new InvalidDataException("Statistics source has a gap or overlap.");
        byte[] pending = Convert.FromBase64String((string)state["partial"]!).Concat(bytes).ToArray();
        var parser = new StatisticsLog(collector, generation, state["parser"]!.AsObject());
        int begin = 0;
        long lineOffset = offset - (pending.Length - bytes.Length);
        for (int i = 0; i < pending.Length; i++)
        if (pending[i] == 10)
        {
            string line = new UTF8Encoding(false, true).GetString(pending, begin, i - begin).TrimEnd('\r');
            foreach (var body in parser.Line(line, lineOffset))
                Queue(body, tx);
            lineOffset += i - begin + 1;
            begin = i + 1;
        }
        if (pending.Length - begin > 65536)
            throw new InvalidDataException("Statistics line exceeds its limit.");
        state["partial"] = Convert.ToBase64String(pending[begin..]);
        state["offset"] = offset + bytes.Length;
        Save(state, tx);
        tx.Commit();
    }
    public void Interrupt(string reason)
    {
        using var tx = db.BeginTransaction();
        var state = Checkpoint(tx);
        var parser = new StatisticsLog(collector, generation, state["parser"]!.AsObject());
        foreach (var body in parser.Interrupt(reason))
            Queue(body, tx);
        Save(state, tx);
        tx.Commit();
    }
    void Save(JsonObject state, SqliteTransaction tx)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE metadata SET data=$data WHERE id=1";
        cmd.Parameters.AddWithValue("$data", state.ToJsonString());
        cmd.ExecuteNonQuery();
    }
    void Queue(JsonObject body, SqliteTransaction tx)
    {
        if (Encoding.UTF8.GetByteCount(body.ToJsonString()) > 256 * 1024)
            throw new InvalidDataException("Statistics observation exceeds its limit.");
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO outbox(id,body) VALUES($id,$body)";
        cmd.Parameters.AddWithValue("$id", (string)body["observationId"]!);
        cmd.Parameters.AddWithValue("$body", body.ToJsonString());
        cmd.ExecuteNonQuery();
    }
    public (string Id, string Body, string State, int Attempts)? Next(long now)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,body,state,attempts FROM outbox WHERE state IN ('pending','received') AND next_attempt<=$now ORDER BY rowid LIMIT 1";
        cmd.Parameters.AddWithValue("$now", now);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3)) : null;
    }
    public void Delivery(string id, string state, long nextAttempt, int attempts)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE outbox SET state=$state,next_attempt=$next,attempts=$attempts WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$next", nextAttempt);
        cmd.Parameters.AddWithValue("$attempts", attempts);
        cmd.ExecuteNonQuery();
    }
    public void ResumeAuthorization() => Execute("UPDATE outbox SET state='pending',next_attempt=0 WHERE state='auth_blocked';");
    public static StatisticsJournal OpenExisting(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT data FROM metadata WHERE id=1";
        var state = JsonNode.Parse((string)cmd.ExecuteScalar()!)!;
        return new(path, (string)state["collector"]!, (string)state["generation"]!);
    }
    public void Dispose() => db.Dispose();
}
