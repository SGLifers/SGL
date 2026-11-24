using SGL.Core;
string cache = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "SGL-runtime-check-" + Guid.NewGuid().ToString("N"));
var files = await ModRuntime.Prepare(cache);
if (!files.ContainsKey("ue4ss/UE4SS.dll.orig") || files.ContainsKey("ue4ss/Mods/BotChatter/enabled.txt")) throw new Exception("Runtime staging enabled a community mod implicitly.");
Console.WriteLine("Verified real pinned UE4SS and Splitgate patch package in isolated staging; no game DLL loaded or game files changed.");
Console.WriteLine("Patched DLL SHA256: " + Json.Hash(files["ue4ss/UE4SS.dll"]));
Console.WriteLine("Prepared runtime files: " + files.Count);
// Verify the exact compatibility package already validated by Prepare. Discovery
// is covered independently by core/desktop tests; CI must not depend on an
// anonymous GitHub API quota shared with other hosted runners.
var pinnedFiles = Mods.ReadZip(File.ReadAllBytes(Path.Combine(cache, Mods.BootstrapHash.ToUpperInvariant() + ".zip")));
var bundle = new ModBundle(new("v1.1.0", Mods.BootstrapUrl, Mods.BootstrapHash, Mods.Discover(pinnedFiles)), pinnedFiles);
if (!bundle.Release.Mods.Any(m => m.Id == "BotChatter") || !bundle.Release.Mods.Any(m => m.Id == "MapEditorBudgetMod")) throw new Exception("Published catalog lost the initial mods.");
string fixture = Path.Combine(Path.GetTempPath(), "SGL-real-mod-files-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
// A text marker makes this an installation fixture, never a runnable game.
File.WriteAllText(Path.Combine(fixture, GameInstallation.Executable), "Non-executable installation fixture");
var store = new ModStore(fixture, Path.Combine(fixture, "SGL-Data"), () => false);
store.Install(bundle, "BotChatter", files);
store.Install(bundle, "MapEditorBudgetMod");
var budget = store.Configurations("MapEditorBudgetMod").Single();
store.Configure("MapEditorBudgetMod", budget, budget.Text.Replace("10000", "12000"));
store.Toggle("BotChatter", false); store.Toggle("MapEditorBudgetMod", false);
if (store.Installed().Any(m => m.Enabled)) throw new Exception("Actual package toggle failed.");
Console.WriteLine("Installed both real release packages, changed the budget and disabled both in a non-executable temporary fixture. No live game files changed.");
