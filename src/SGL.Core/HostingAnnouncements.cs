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
        if (!Regex.IsMatch(code, @"\A[A-Z0-9][A-Z0-9-]{3,31}\z")) throw new InvalidDataException("Enter a lobby code using 4–32 letters, numbers or hyphens.");
        return code;
    }
    public static Uri Endpoint(string url)
    {
        if (!Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "" || !uri.IsDefaultPort) throw new InvalidDataException("The hosting service must use HTTPS without credentials, query parameters or a custom port.");
        return uri;
    }
    public static async Task<JsonObject> Announce(string endpoint, string token, string code, string requestId, CancellationToken cancel = default, HttpMessageHandler? handler = null)
    {
        Uri root = Endpoint(endpoint);
        code = Code(code);
        if (!Regex.IsMatch(requestId, @"\A[a-f0-9]{32}\z") || !Regex.IsMatch(token, @"\A[A-Za-z0-9_-]{32,512}\z")) throw new InvalidDataException("Invalid hosting session or request identity.");
        using var client = handler == null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler, false);
        client.Timeout = TimeSpan.FromSeconds(20);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(root, "announcements"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("SGL-Hosting/1.0");
        request.Content = new StringContent(new JsonObject { ["code"] = code, ["requestId"] = requestId }.ToJsonString(), Encoding.UTF8, "application/json");
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
            string message = response.StatusCode switch { HttpStatusCode.Unauthorized => "Sign in with Discord again.", HttpStatusCode.Forbidden => "Your Discord account is not permitted to announce in this server.", HttpStatusCode.TooManyRequests => "Announcements are cooling down.", HttpStatusCode.Conflict => "Delivery needs review. Check the Discord channel before retrying.", _ => "The announcement service is unavailable." };
            throw new HostingApiException(response.StatusCode, message, retry);
        }
        if ((string?)body["status"] is not ("sent" or "duplicate")) throw new IOException("Announcement was not confirmed.");
        return body;
    }
}
