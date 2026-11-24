using System.Text.RegularExpressions;
using Microsoft.Win32;
namespace SGL.Core;

public static class GameInstallation
{
    public const string Executable = "PortalWars2-Win64-Shipping.exe";
    public static string Resolve(string folder)
    {
        foreach (string relative in new[] { "", "Binaries/Win64", "PortalWars2/Binaries/Win64" })
        {
            string path = Path.GetFullPath(Path.Combine(folder, relative));
            if (File.Exists(Path.Combine(path, Executable))) return path;
        }
        throw new DirectoryNotFoundException("Choose your Splitgate game folder or PortalWars2\\Binaries\\Win64 folder. This is separate from CloudSave.");
    }
    public static string[] Find()
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in new[] { Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string,
                Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string })
                if (!string.IsNullOrWhiteSpace(path)) libraries.Add(path);
        }
        catch { }
        foreach (string steam in libraries.ToArray())
        {
            try
            {
                string vdf = File.ReadAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"));
                foreach (Match match in Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\"")) libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
            }
            catch { }
        }
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string library in libraries)
        {
            try
            {
                foreach (string manifest in Directory.EnumerateFiles(Path.Combine(library, "steamapps"), "appmanifest_*.acf").Take(2000))
                {
                    string text = File.ReadAllText(manifest);
                    var match = Regex.Match(text, "\"installdir\"\\s+\"([^\"]+)\"");
                    if (!match.Success || !Maps.ValidId(match.Groups[1].Value.Replace(' ', '_'))) continue;
                    try { result.Add(Resolve(Path.Combine(library, "steamapps", "common", match.Groups[1].Value))); } catch { }
                }
            }
            catch { }
        }
        return result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
