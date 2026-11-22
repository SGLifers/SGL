using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
if (args.Length < 1) throw new ArgumentException("Use keygen <private-file> <public-file>, or sign <exe> <version> <output>.");
if (args[0] == "keygen")
{
    if (File.Exists(args[1]))
        throw new IOException("Private key already exists; refusing to replace it.");
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(args[1], key.ExportPkcs8PrivateKeyPem());
    File.WriteAllText(args[2], key.ExportSubjectPublicKeyInfoPem());
    Console.WriteLine("Created verification key. Keep the private key outside source control.");
    return;
}
if (args[0] != "sign" || args.Length != 4) throw new ArgumentException("Invalid signing arguments.");
string pem = Environment.GetEnvironmentVariable("SGL_UPDATE_PRIVATE_KEY") ?? File.ReadAllText(Environment.GetEnvironmentVariable("SGL_SIGNING_KEY_FILE") ?? throw new ArgumentException("Set SGL_UPDATE_PRIVATE_KEY or SGL_SIGNING_KEY_FILE."));
using (var key = ECDsa.Create())
{
    key.ImportFromPem(pem);
    byte[] exe = File.ReadAllBytes(args[1]);
    var version = Version.Parse(args[2]);
    byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = version.ToString(),
        url = $"https://github.com/SGLifers/SGL/releases/download/v{version}/SGL.exe",
        sha256 = Convert.ToHexString(SHA256.HashData(exe)),
        size = exe.LongLength
    });
    byte[] signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    File.WriteAllText(args[3], JsonSerializer.Serialize(new
    {
        payload = Convert.ToBase64String(payload),
        signature = Convert.ToBase64String(signature)
    }));
    Console.WriteLine("Signed update manifest for " + version);
}
