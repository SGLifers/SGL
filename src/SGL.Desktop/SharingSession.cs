using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
namespace SGL.Desktop;

sealed class SharingSession(string path, string service)
{
    byte[] Entropy => SHA256.HashData(Encoding.UTF8.GetBytes(service.TrimEnd('/')));
    public JsonObject? Load()
    {
        if (!File.Exists(path))
            return null;
        try
        {
            byte[] bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            var doc = SGL.Core.Json.Read(Encoding.UTF8.GetString(bytes)).AsObject();
            return (long)doc["expiresAt"]! > DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? doc : null;
        }
        catch { return null; }
    }
    public void Save(JsonObject session)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(session.ToJsonString()), Entropy, DataProtectionScope.CurrentUser);
        string temp = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Clear()
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
