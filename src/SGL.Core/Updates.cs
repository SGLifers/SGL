using System.Security.Cryptography;
using System.Text.Json.Nodes;
namespace SGL.Core;

public sealed record UpdateRelease(Version Version, Uri Download, string Sha256, long Size);
public static class Updates
{
    public const string Repository = "SGLifers/SGL";
    public const string Feed = "https://github.com/" + Repository + "/releases/latest/download/update.json";
    public static UpdateRelease Verify(string envelope, string publicKey)
    {
        if (envelope.Length > 131072)
            throw new InvalidDataException("Update manifest is too large.");
        var doc = Json.Read(envelope);
        byte[] payload = Convert.FromBase64String((string)doc["payload"]!), signature = Convert.FromBase64String((string)doc["signature"]!);
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKey);
        if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw new InvalidDataException("Update signature could not be verified.");
        var data = Json.Read(System.Text.Encoding.UTF8.GetString(payload));
        var version = Version.Parse((string)data["version"]!);
        var url = new Uri((string)data["url"]!);
        if (url.Scheme != "https" || url.Host != "github.com" || !url.AbsolutePath.StartsWith("/" + Repository + "/releases/download/", StringComparison.Ordinal) || url.Query != "" || url.Fragment != "")
            throw new InvalidDataException("Update URL is not an official SGL release.");
        string sha = (string)data["sha256"]!;
        long size = (long)data["size"]!;
        if (sha.Length != 64 || !sha.All(Uri.IsHexDigit) || size < 1 || size > 400_000_000)
            throw new InvalidDataException("Invalid update package metadata.");
        return new(version, url, sha, size);
    }
    public static bool CheckFile(string path, UpdateRelease release) => new FileInfo(path).Length == release.Size && Json.Hash(File.ReadAllBytes(path)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase);
    public static void EnsureWindowsExecutable(string path)
    {
        using var file = File.OpenRead(path);
        using var reader = new BinaryReader(file);
        if (file.Length < 128 || reader.ReadUInt16() != 0x5A4D)
            throw new InvalidDataException("Update is not a Windows executable.");
        file.Position = 0x3c;
        int pe = reader.ReadInt32();
        if (pe < 64 || pe > file.Length - 26)
            throw new InvalidDataException("Invalid executable header.");
        file.Position = pe;
        if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x8664)
            throw new InvalidDataException("Update is not a Windows x64 executable.");
        file.Position = pe + 22;
        if ((reader.ReadUInt16() & 0x2000) != 0 || reader.ReadUInt16() != 0x20b)
            throw new InvalidDataException("Update is not a standalone x64 application.");
    }
    public static async Task<(UpdateRelease Release, string Package, string Envelope)?> Download(Version current, string directory, string publicKey, CancellationToken cancel = default, HttpMessageHandler? handler = null)
    {
        using var client = handler == null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SGL/" + current);
        var json = await client.GetStringAsync(Feed, cancel);
        var release = Verify(json, publicKey);
        if (release.Version <= current)
            return null;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, "download.tmp"), package = Path.Combine(directory, "SGL.exe");
        using (var response = await client.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != release.Size)
                throw new InvalidDataException("Update size mismatch.");
            await using var input = await response.Content.ReadAsStreamAsync(cancel);
            await using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[65536];
            long total = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, cancel)) > 0)
            {
                total += count;
                if (total > release.Size)
                    throw new InvalidDataException("Update exceeded its expected size.");
                await output.WriteAsync(buffer.AsMemory(0, count), cancel);
            }
        }
        if (!CheckFile(temp, release))
        {
            File.Delete(temp);
            throw new InvalidDataException("Update checksum mismatch. Installed version retained.");
        }
        File.Move(temp, package, true);
        Json.Atomic(Path.Combine(directory, "update.json"), json);
        return (release, package, json);
    }
}
