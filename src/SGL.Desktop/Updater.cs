using System.Diagnostics;
using SGL.Core;
namespace SGL.Desktop;

static class Updater
{
    public static string PublicKey => Modes.Resource("update-public-key.txt");
    public static void Begin(string staged, string dataRoot)
    {
        string current = Environment.ProcessPath ?? throw new IOException("Application path unavailable.");
        string helper = Path.Combine(Path.GetDirectoryName(staged)!, "SGL.UpdateHelper.exe");
        File.Copy(current, helper, true);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "--apply-update", Environment.ProcessId.ToString(), current, staged, dataRoot })
            start.ArgumentList.Add(arg);
        _ = Process.Start(start) ?? throw new IOException("Update helper was blocked. The current version remains installed.");
    }
    public static void Apply(string[] args)
    {
        if (args.Length != 5)
            throw new ArgumentException("Invalid update arguments.");
        string target = Path.GetFullPath(args[2]), staged = Path.GetFullPath(args[3]);
        var release = Updates.Verify(File.ReadAllText(Path.Combine(Path.GetDirectoryName(staged)!, "update.json")), PublicKey);
        if (!Updates.CheckFile(staged, release))
            throw new IOException("Update package changed after verification.");
        Updates.EnsureWindowsExecutable(staged);
        try
        {
            var parent = Process.GetProcessById(int.Parse(args[1]));
            if (!parent.WaitForExit(20000))
                return;
        }
        catch (ArgumentException) { }
        string previous = target + ".previous", temp = target + ".update", health = Path.Combine(Path.GetDirectoryName(staged)!, "ready-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.Copy(staged, temp, true);
            File.Replace(temp, target, previous, true);
            var start = new ProcessStartInfo(target) { UseShellExecute = false };
            start.ArgumentList.Add("--update-health");
            start.ArgumentList.Add(health);
            start.ArgumentList.Add("--data");
            start.ArgumentList.Add(Path.GetFullPath(args[4]));
            start.ArgumentList.Add("--no-update");
            var process = Process.Start(start) ?? throw new IOException("Updated application could not start.");
            for (int i = 0; i < 200 && !File.Exists(health) && !process.HasExited; i++)
                Thread.Sleep(100);
            if (File.Exists(health))
                return;
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(5000);
            }
            throw new IOException("Updated application did not report healthy startup.");
        }
        catch
        {
            if (File.Exists(previous))
            {
                File.Copy(previous, target, true);
                var recover = new ProcessStartInfo(target) { UseShellExecute = false };
                recover.ArgumentList.Add("--data");
                recover.ArgumentList.Add(Path.GetFullPath(args[4]));
                recover.ArgumentList.Add("--no-update");
                Process.Start(recover);
            }
            throw;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
