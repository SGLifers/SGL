using System.IO.Compression;
using SGL.Core;

static class InstalledPreviewChecks
{
    public static void Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "sgl-preview-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string Write(string name, params (string Name, byte[] Data)[] entries)
        {
            string path = Path.Combine(folder, name + ".bin");
            using var file = File.Create(path);
            using var zip = new ZipArchive(file, ZipArchiveMode.Create);
            using (var world = zip.CreateEntry("World.cf1047").Open()) world.Write([1, 2, 3]);
            foreach (var entry in entries)
                using (var output = zip.CreateEntry(entry.Name).Open()) output.Write(entry.Data);
            return path;
        }
        byte[] screenshot = [1, 2, 3, 4, 5];
        string local = Write("local", ("Screenshot.jpg", screenshot));
        byte[] before = File.ReadAllBytes(local);
        check(Maps.InstalledPreview(local)!.SequenceEqual(screenshot), "Installed screenshot bytes changed");
        check(File.ReadAllBytes(local).SequenceEqual(before), "Reading a screenshot modified the map archive");
        check(Maps.InstalledPreview(Write("uppercase", ("SCREENSHOT.JPG", screenshot)))!.SequenceEqual(screenshot), "Case-insensitive native screenshot lookup failed");
        check(Maps.InstalledPreview(Write("png", ("Screenshot.png", screenshot)))!.SequenceEqual(screenshot), "PNG screenshot fallback failed");
        check(Maps.InstalledPreview(Write("jpeg", ("Screenshot.jpeg", screenshot)))!.SequenceEqual(screenshot), "JPEG screenshot fallback failed");
        check(Maps.InstalledPreview(Write("priority", ("Screenshot.png", [9]), ("Screenshot.jpg", screenshot)))!.SequenceEqual(screenshot), "Native JPG screenshot did not take priority");
        check(Maps.InstalledPreview(Write("missing")) == null, "Missing screenshot did not return a placeholder result");
        check(Maps.InstalledPreview(Write("empty", ("Screenshot.jpg", []))) == null, "Empty screenshot did not return a placeholder result");
        check(Maps.InstalledPreview(Write("nested", ("other/Screenshot.jpg", screenshot))) == null, "Unrelated nested file was treated as map screenshot");
        bool tooLarge = false;
        try { Maps.InstalledPreview(Write("oversize", ("Screenshot.jpg", new byte[8_000_001]))); }
        catch (InvalidDataException) { tooLarge = true; }
        check(tooLarge, "Oversized screenshot was read into memory");
        using var held = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        check(Maps.InstalledPreview(local)!.SequenceEqual(screenshot), "Read-only screenshot preview unnecessarily required exclusive map access");
    }
}
