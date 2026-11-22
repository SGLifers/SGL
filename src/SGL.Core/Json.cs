using System.Text.Json;
using System.Text.Json.Nodes;
using System.Numerics;
using System.Globalization;
namespace SGL.Core;

public static class Json
{
    public static JsonNode Read(string text) => JsonNode.Parse(text, documentOptions: new() { MaxDepth = 100 }) ?? throw new InvalidDataException("Empty JSON.");
    public static string Canonical(JsonNode? node) => Sort(node)?.ToJsonString(new() { WriteIndented = true }) ?? "null";
    static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sort(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Sort).ToArray()),
        JsonValue value when value.GetValueKind() == JsonValueKind.Number => NormalizeNumber(value.ToJsonString()),
        _ => node?.DeepClone()
    };
    static JsonNode NormalizeNumber(string raw)
    {
        bool negative = raw.StartsWith('-');
        string text = negative ? raw[1..] : raw;
        int e = text.IndexOfAny(['e', 'E']);
        BigInteger exponent = e < 0 ? 0 : BigInteger.Parse(text[(e + 1)..], CultureInfo.InvariantCulture);
        if (e >= 0)
            text = text[..e];
        int dot = text.IndexOf('.');
        if (dot >= 0)
        {
            exponent -= text.Length - dot - 1;
            text = text.Remove(dot, 1);
        }
        text = text.TrimStart('0');
        if (text.Length == 0)
            return JsonValue.Create(0)!;
        int length = text.Length;
        text = text.TrimEnd('0');
        exponent += length - text.Length;
        string normalized;
        if (exponent > 1000 || exponent < -1000)
            normalized = text + "e" + exponent.ToString(CultureInfo.InvariantCulture);
        else
        {
            int n = (int)exponent, position = text.Length + n;
            normalized = n >= 0 ? text + new string('0', n) : position > 0 ? text.Insert(position, ".") : "0." + new string('0', -position) + text;
        }
        return Read((negative ? "-" : "") + normalized);
    }
    public static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    public static void Atomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

// Compatibility adapter for the independently tested legacy compiler. JSON values
// remain CLR dictionaries/arrays so payload mappings retain their existing behavior.
internal sealed class JavaScriptSerializer
{
    public int MaxJsonLength { get; set; } = 16000000;
    public object DeserializeObject(string text)
    {
        if (text.Length > MaxJsonLength)
            throw new InvalidDataException("JSON is too large.");
        using var doc = JsonDocument.Parse(text, new()
        {
            MaxDepth = 100
        });
        return ConvertValue(doc.RootElement)!;
    }
    static object? ConvertValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Object => v.EnumerateObject().ToDictionary(p => p.Name, p => ConvertValue(p.Value)!),
        JsonValueKind.Array => v.EnumerateArray().Select(ConvertValue).ToArray(),
        JsonValueKind.String => v.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => v.TryGetInt32(out var i) ? (object)i : v.TryGetInt64(out var l) ? l : v.GetDouble(),
        _ => null
    };
    public string Serialize(object? value) => JsonSerializer.Serialize(value);
}
