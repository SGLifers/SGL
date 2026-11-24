using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
namespace SGL.Core;

public static class ModRuntime
{
    public static async Task<Dictionary<string, byte[]>> Prepare(string cache, CancellationToken cancel = default, HttpMessageHandler? handler = null)
    {
        async Task<byte[]> Package(string url, string hash)
        {
            Directory.CreateDirectory(cache);
            string path = Path.Combine(cache, hash.ToUpperInvariant() + ".zip");
            byte[] bytes = File.Exists(path) ? await File.ReadAllBytesAsync(path, cancel) : await Mods.Fetch(url, 32_000_000, cancel, handler);
            if (!Json.Hash(bytes).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Pinned UE4SS dependency checksum mismatch.");
            await File.WriteAllBytesAsync(path, bytes, cancel);
            return bytes;
        }
        var runtime = Mods.ReadZip(await Package(Mods.RuntimeUrl, Mods.RuntimeHash));
        var overlay = Mods.ReadZip(await Package(Mods.BootstrapUrl, Mods.BootstrapHash));
        byte[] patcher = overlay["install_patch.bat"];
        if (!Json.Hash(patcher).Equals(Mods.PatcherHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unknown UE4SS patcher.");
        string stage = Path.Combine(Path.GetTempPath(), "SGL-UE4SS-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var file in overlay.Where(p => p.Key.StartsWith("ue4ss/", StringComparison.OrdinalIgnoreCase) && !p.Key.StartsWith("ue4ss/Mods/", StringComparison.OrdinalIgnoreCase))) runtime[file.Key] = file.Value;
            foreach (var file in runtime)
            {
                string target = GameStore.Inside(stage, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, file.Value, cancel);
            }
            // The upstream patcher requires this filename to verify its working folder.
            // This empty staging marker is never executed. No game files are copied or patched.
            File.WriteAllBytes(Path.Combine(stage, "PortalWars2-Win64-Shipping.exe"), []);
            string source = Encoding.UTF8.GetString(patcher), marker = "###__PS_START__###";
            int index = source.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0) throw new InvalidDataException("Pinned patcher has no PowerShell payload.");
            string script = Path.Combine(stage, "patch.ps1");
            await File.WriteAllTextAsync(script, source[(index + marker.Length)..], new UTF8Encoding(false), cancel);
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = stage
            };
            // Use Windows PowerShell's system modules even when launched from PowerShell 7.
            start.Environment["PSModulePath"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "Modules");
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-BatDir", stage }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("Could not start the pinned UE4SS patcher.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
            await Task.WhenAll(output, error);
            if (process.ExitCode != 0) throw new InvalidDataException("The pinned UE4SS patcher failed in staging. Game files were not changed. " + error.Result[..Math.Min(error.Result.Length, 1200)]);
            var files = Directory.GetFiles(stage, "*", SearchOption.AllDirectories)
                .Select(p => (Path: p, Relative: Path.GetRelativePath(stage, p).Replace('\\', '/')))
                .Where(p => p.Relative == "dwmapi.dll" || p.Relative.StartsWith("ue4ss/", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(p => p.Relative, p => File.ReadAllBytes(p.Path), StringComparer.OrdinalIgnoreCase);
            if (!files.ContainsKey("dwmapi.dll") || !files.ContainsKey("ue4ss/UE4SS.dll.orig") || files["ue4ss/UE4SS.dll"].SequenceEqual(files["ue4ss/UE4SS.dll.orig"])) throw new InvalidDataException("UE4SS staging did not produce the patched runtime.");
            if (!Json.Hash(files["ue4ss/UE4SS.dll"]).Equals(Mods.PatchedRuntimeHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Patched UE4SS output does not match the verified build.");
            // Install the shared helpers, but do not enable bundled example/console mods.
            files["ue4ss/Mods/mods.txt"] = Encoding.UTF8.GetBytes("; Managed entries are updated by SGL. Other entries are preserved.\nKeybinds : 1\n");
            string settings = Encoding.UTF8.GetString(files["ue4ss/UE4SS-settings.ini"]);
            settings = Regex.Replace(settings, @"(?m)^(ConsoleEnabled|GuiConsoleEnabled|GuiConsoleVisible)\s*=.*$", "$1 = 0");
            files["ue4ss/UE4SS-settings.ini"] = Encoding.UTF8.GetBytes(settings);
            files["ue4ss/SGL-Splitgate-Mods-LICENSE.txt"] = Encoding.UTF8.GetBytes(Modes.Resource("mods-LICENSE.txt"));
            return files;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
}
