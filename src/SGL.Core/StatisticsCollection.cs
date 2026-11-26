using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
namespace SGL.Core;

public sealed class StatisticsCollection : IAsyncDisposable
{
    readonly string root, actor, token, endpoint, path;
    readonly Action<string> status;
    readonly CancellationTokenSource stop = new();
    readonly HttpMessageHandler? transport;
    Task? work;
    int deliveryCursor;
    bool uploadAuthorizationBlocked;
    public static string DefaultLog => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortalWars2", "Saved", "Logs", "PortalWars2.log");
    public StatisticsCollection(string root, string actor, string token, string endpoint, string path, Action<string> status, HttpMessageHandler? transport = null)
    {
        this.root = root;
        this.actor = actor;
        this.token = token;
        this.endpoint = endpoint;
        this.path = Path.GetFullPath(path);
        this.status = status;
        this.transport = transport;
    }
    public void Start() => work ??= Task.Run(Run);
    static Process? Game()
    {
        var games = new[] { "PortalWars2-Win64-Shipping", "PortalWars2" }.SelectMany(Process.GetProcessesByName).ToArray();
        if (games.Length == 1)
            return games[0];
        foreach (var game in games)
            game.Dispose();
        return null;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct FileIdentity
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileIdentity information);
    static string Identity(FileStream file)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Live statistics collection requires Windows file identity.");
        if (!GetFileInformationByHandle(file.SafeFileHandle, out var info) || info.Links != 1)
            throw new IOException("Statistics log identity is unavailable.");
        return $"{info.Volume}:{info.IndexHigh}:{info.IndexLow}:{info.CreationHigh}:{info.CreationLow}";
    }
    async Task Run()
    {
        StatisticsJournal? current = null;
        Process? owner = null;
        FileStream? source = null;
        string? identity = null;
        DateTime processStart = default;
        byte[] prefixHash = [];
        long offset = 0;
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            string directory = Path.Combine(root, "Statistics", StatisticsLog.Hash(actor));
            Directory.CreateDirectory(directory);
            string idPath = Path.Combine(directory, "collector-id.txt");
            if (!File.Exists(idPath))
                File.WriteAllText(idPath, Guid.NewGuid().ToString("N"));
            string collector = File.ReadAllText(idPath).Trim();
            foreach (string old in Directory.EnumerateFiles(directory, "*.sqlite"))
            {
                using var journal = StatisticsJournal.OpenExisting(old);
                journal.Interrupt("collector_restart");
                journal.ResumeAuthorization();
            }
            while (!stop.IsCancellationRequested)
            {
                if (source is null)
                {
                    owner = Game();
                    if (owner is not null && File.Exists(path))
                    {
                        processStart = owner.StartTime.ToUniversalTime();
                        if (File.GetLastWriteTimeUtc(path) >= processStart)
                        {
                            source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            identity = Identity(source);
                            offset = source.Length;
                            if (offset > 128 * 1024 * 1024)
                                throw new IOException("Statistics log exceeds its supported size.");
                            prefixHash = HashPrefix(source, offset);
                            string generation = StatisticsLog.Hash(identity + ":" + owner.Id + ":" + processStart.Ticks + ":" + Guid.NewGuid().ToString("N"));
                            current = new StatisticsJournal(Path.Combine(directory, generation + ".sqlite"), collector, generation, offset);
                            status("Waiting for the next authoritative game match…");
                        }
                    }
                    if (source is null)
                    {
                        owner?.Dispose();
                        owner = null;
                        status("Waiting for Splitgate and its current game log…");
                    }
                }
                else
                {
                    if (owner is null || owner.HasExited || owner.StartTime.ToUniversalTime() != processStart)
                        throw new IOException("The statistics game process ended.");
                    using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        if (Identity(probe) != identity)
                            throw new IOException("The statistics log was replaced.");
                    if (source.Length < offset || source.Length > 128 * 1024 * 1024 || !CryptographicOperations.FixedTimeEquals(prefixHash, HashPrefix(source, offset)))
                        throw new IOException("Statistics log continuity changed.");
                    long size = source.Length;
                    int count = (int)Math.Min(256 * 1024, size - offset);
                    if (count > 0)
                    {
                        source.Position = offset;
                        byte[] bytes = new byte[count];
                        source.ReadExactly(bytes);
                        // Verify the old prefix again; concurrent append is allowed, rewrite is not.
                        if (!CryptographicOperations.FixedTimeEquals(prefixHash, HashPrefix(source, offset)))
                            throw new IOException("Statistics log changed during read.");
                        source.Position = offset;
                        byte[] confirmed = new byte[count];
                        source.ReadExactly(confirmed);
                        if (!bytes.AsSpan().SequenceEqual(confirmed))
                            throw new IOException("Statistics log changed during read.");
                        byte[] nextHash = HashPrefix(source, offset + count);
                        current!.Ingest(bytes, offset);
                        offset += count;
                        prefixHash = nextHash;
                        status("Collecting game-log stats. Combat stats await a later source.");
                    }
                }
                if (!string.IsNullOrWhiteSpace(endpoint) && !uploadAuthorizationBlocked)
                    await Upload(directory, stop.Token);
                await Task.Delay(TimeSpan.FromSeconds(2), stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException)
        {
            status("Stats collection stopped: the current game/log could not be verified. Reopen the lobby to start a new session.");
        }
        finally
        {
            try
            {
                current?.Interrupt("collection_stopped");
            }
            catch { status("Stats checkpoint needs review; existing queued results were retained."); }
            current?.Dispose();
            source?.Dispose();
            owner?.Dispose();
        }
    }
    static byte[] HashPrefix(FileStream source, long count)
    {
        source.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long remaining = count;
        while (remaining > 0)
        {
            int n = source.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
            if (n == 0)
                throw new IOException("Statistics log truncated.");
            hash.AppendData(buffer, 0, n);
            remaining -= n;
        }
        return hash.GetHashAndReset();
    }
    internal async Task Upload(string directory, CancellationToken cancel)
    {
        if (!Uri.TryCreate(endpoint.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "" || !uri.IsDefaultPort)
            throw new InvalidDataException("Invalid statistics endpoint.");
        using var client = transport is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(transport, false);
        client.Timeout = TimeSpan.FromSeconds(15);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string[] files = Directory.EnumerateFiles(directory, "*.sqlite").OrderBy(x => x, StringComparer.Ordinal).ToArray();
        for (int index = 0; index < Math.Min(8, files.Length); index++)
        {
            string file = files[deliveryCursor % files.Length];
            deliveryCursor = (deliveryCursor + 1) % files.Length;
            using var journal = StatisticsJournal.OpenExisting(file);
            var next = journal.Next(now);
            if (next is not { } item)
                continue;
            using var request = new HttpRequestMessage(item.State == "received" ? HttpMethod.Get : HttpMethod.Post, new Uri(uri, item.State == "received" ? "v1/stats/receipts/" + item.Id : "v1/stats/observations"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Idempotency-Key", item.Id);
            if (item.State != "received")
                request.Content = new StringContent(item.Body, Encoding.UTF8, "application/json");
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                string state = "pending";
                long retry = now + Math.Min(300, 5 * (1L << Math.Min(item.Attempts, 5)));
                if (response.IsSuccessStatusCode)
                {
                    using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
                    using var output = new MemoryStream();
                    byte[] buffer = new byte[2048];
                    int n;
                    while ((n = await input.ReadAsync(buffer, deadline.Token)) > 0)
                    {
                        if (output.Length + n > 32768)
                            throw new IOException("Statistics response too large.");
                        output.Write(buffer, 0, n);
                    }
                    var body = JsonNode.Parse(output.ToArray()) as JsonObject;
                    string? server = null;
                    string? receiptId = null;
                    if (body?["status"] is JsonValue statusValue)
                        statusValue.TryGetValue<string>(out server);
                    if (body?["id"] is JsonValue idValue)
                        idValue.TryGetValue<string>(out receiptId);
                    if (receiptId != item.Id || server is not ("received" or "processing" or "published" or "needs_review" or "rejected"))
                        throw new IOException("Invalid statistics receipt.");
                    state = server is "published" or "rejected" or "needs_review" ? server : "received";
                    retry = now + 30;
                }
                else if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    state = "auth_blocked";
                    uploadAuthorizationBlocked = true;
                    status("Stats uploads paused. Sign in with a permitted host account to resume.");
                }
                else if ((int)response.StatusCode is >= 300 and < 400 || (int)response.StatusCode is >= 400 and < 500 && (int)response.StatusCode is not (408 or 425 or 429))
                    state = "rejected";
                if (response.Headers.RetryAfter?.Delta is TimeSpan delay)
                    retry = Math.Max(retry, now + (long)Math.Clamp(delay.TotalSeconds, 1, 900));
                journal.Delivery(item.Id, state, retry + Random.Shared.Next(0, 5), item.Attempts + 1);
                if (uploadAuthorizationBlocked)
                    return;
            }
            catch (Exception error) when (error is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException && !cancel.IsCancellationRequested)
            {
                journal.Delivery(item.Id, item.State, now + 30, item.Attempts + 1);
                status("Stats queued locally; upload will retry.");
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (work is not null)
            await work;
        stop.Dispose();
    }
}
