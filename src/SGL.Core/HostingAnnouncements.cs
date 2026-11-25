using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace SGL.Core;

public sealed class HostingApiException(HttpStatusCode status, string message, int retryAfter = 0) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public int RetryAfter { get; } = retryAfter;
}
public static class HostingAnnouncements
{
    public static string Code(string text)
    {
        string code = text.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(code, @"\A[A-Z0-9]{6}\z")) throw new InvalidDataException("Enter the six-character lobby code using letters or numbers.");
        return code;
    }
    public static string LobbyName(string text)
    {
        string name = text.Trim();
        if (name.Length > 80 || name.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator))
            throw new InvalidDataException("Use a lobby name up to 80 characters on one line.");
        return name;
    }
    public static string Gamemode(string text)
    {
        string mode = text.Trim();
        if (mode.Length > 120 || mode.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator))
            throw new InvalidDataException("Use a gamemode up to 120 characters on one line.");
        return mode;
    }
    public static Uri Endpoint(string url)
    {
        if (!Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "" || !uri.IsDefaultPort) throw new InvalidDataException("The hosting service must use HTTPS without credentials, query parameters or a custom port.");
        return uri;
    }
    public static async Task<JsonObject> Announce(string endpoint, string token, string code, string requestId, CancellationToken cancel = default, HttpMessageHandler? handler = null, string lobbyName = "", string action = "open", string gamemode = "", string? announcementId = null)
    {
        Uri root = Endpoint(endpoint);
        code = Code(code);
        lobbyName = LobbyName(lobbyName);
        gamemode = Gamemode(gamemode);
        if (action is not ("open" or "close" or "update")) throw new InvalidDataException("Choose Open Lobby or Close Lobby.");
        if (!Regex.IsMatch(requestId, @"\A[a-f0-9]{32}\z") || !Regex.IsMatch(token, @"\A[A-Za-z0-9_-]{32,512}\z")) throw new InvalidDataException("Invalid hosting session or request identity.");
        if (action == "update" && (announcementId == null || !Regex.IsMatch(announcementId, @"\A[a-f0-9]{32}\z"))) throw new InvalidDataException("Open the lobby before updating its gamemode.");
        using var client = handler == null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler, false);
        client.Timeout = TimeSpan.FromSeconds(20);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(root, "announcements"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("SGL-Hosting/1.0");
        var payload = new JsonObject { ["code"] = code, ["requestId"] = requestId, ["lobbyName"] = lobbyName, ["action"] = action };
        if (gamemode.Length > 0 || action == "update") payload["gamemode"] = gamemode;
        if (action == "update") payload["announcementId"] = announcementId;
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (response.Content.Headers.ContentLength > 8192) throw new IOException("Invalid hosting response.");
        using var input = await response.Content.ReadAsStreamAsync(cancel);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[2048]; int read;
        while ((read = await input.ReadAsync(buffer, cancel)) > 0) { if (bytes.Length + read > 8192) throw new IOException("Invalid hosting response."); bytes.Write(buffer, 0, read); }
        JsonObject body;
        try { body = Json.Read(Encoding.UTF8.GetString(bytes.ToArray())).AsObject(); } catch { throw new IOException("Invalid hosting response."); }
        if (!response.IsSuccessStatusCode)
        {
            int retry = Math.Clamp((int?)body["retryAfter"] ?? 0, 0, 120);
            // Do not render arbitrary server-provided text or credential-bearing errors.
            string message = response.StatusCode switch { HttpStatusCode.Unauthorized => "Sign in with Discord again.", HttpStatusCode.Forbidden => "Your Discord account is not permitted to announce in this server.", HttpStatusCode.NotFound => "No recent announcement for this lobby was found for your account.", HttpStatusCode.TooManyRequests => "Lobby requests are cooling down.", HttpStatusCode.Conflict => "Delivery needs review. Check the Discord channel before retrying.", _ => "The lobby service is unavailable." };
            throw new HostingApiException(response.StatusCode, message, retry);
        }
        if ((string?)body["status"] is not ("sent" or "duplicate" or "closed" or "updated") || action == "close" && (string?)body["status"] != "closed") throw new IOException("Lobby request was not confirmed.");
        if (action == "update" && (string?)body["status"] is not ("updated" or "duplicate")) throw new IOException("Gamemode update was not confirmed.");
        if (body["announcementId"] != null && !Regex.IsMatch((string?)body["announcementId"] ?? "", @"\A[a-f0-9]{32}\z")) throw new IOException("Invalid announcement identity.");
        return body;
    }
}
