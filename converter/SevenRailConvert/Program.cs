// 7Rail converter: reads EverRail's own models, textures, sounds and train rules from the player's EverRail
// install and writes them into a cache the 7 Days to Die mod loads. Nothing from EverRail ships with 7Rail.
//
// usage: 7rail-convert [--everrail <EverRail folder>] --out <cache folder> [--done <done file>]
// Without --everrail it looks for EverRail (Demo or full game) in Steam's library folders.
using System.Text.Json;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Versions;
using SevenRail.Convert;

var opts = Args.Parse(args);
var log = new List<string>();
void Log(string s) { Console.WriteLine(s); log.Add(s); }

var everrail = opts.GetValueOrDefault("everrail") ?? SteamLocator.FindEverRail();
var outDir = opts.GetValueOrDefault("out") ?? throw new ArgumentException("--out is required");
var done = opts.GetValueOrDefault("done");
Directory.CreateDirectory(outDir);

if (everrail == null || SteamLocator.PaksDir(everrail) == null)
{
    Log("EverRail was not found on this PC (looked for EverRail/Content/Paks in Steam's library folders).");
    File.WriteAllText(Path.Combine(outDir, "missing.json"), JsonSerializer.Serialize(new { missing = true, looked = SteamLocator.Searched }));
    return 2;
}
var paks = SteamLocator.PaksDir(everrail)!;
Log($"EverRail: {everrail}");

var usmap = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "mappings"), "*.usmap").OrderBy(f => f).Last();
var provider = new DefaultFileProvider(paks, SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_UE5_7));
provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmap);
provider.Initialize();
provider.Mount();
Log($"mounted {provider.Files.Count} files with {Path.GetFileName(usmap)}");

var results = new Dictionary<string, string>();
foreach (var row in EverrailAssetsRow.All)
{
    try
    {
        switch (row.Kind)
        {
            case "mesh": MeshExport.Run(provider, row, outDir, Log); break;
            case "sound": SoundExport.Run(provider, row, outDir, Log); break;
            case "table": break; // read by RulesExport below
        }
        results[row.Id] = "ok";
    }
    catch (Exception e)
    {
        results[row.Id] = $"{e.GetType().Name}: {e.Message}";
        Log($"!! {row.Id}: {e.GetType().Name}: {e.Message}");
    }
}

foreach (var layout in LayoutsRow.All)
{
    try { LayoutExport.Run(provider, layout, outDir, Log); results[layout.Id] = "ok"; }
    catch (Exception e) { results[layout.Id] = $"{e.GetType().Name}: {e.Message}"; Log($"!! {layout.Id}: {e}"); }
}

try
{
    RulesExport.Run(provider, outDir, Log);
    results["rules"] = "ok";
}
catch (Exception e)
{
    results["rules"] = $"{e.GetType().Name}: {e.Message}";
    Log($"!! rules: {e}");
}

var failed = results.Where(r => r.Value != "ok").Select(r => r.Key).ToList();
var report = new { converter = "7rail-convert 0.1.0", everrail, mappings = Path.GetFileName(usmap), results, failed, log };
File.WriteAllText(Path.Combine(outDir, "convert-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
File.Delete(Path.Combine(outDir, "missing.json"));
if (failed.Count > 0)
{
    Log($"{failed.Count} item(s) failed: {string.Join(", ", failed)}");
    return 1;
}
if (done != null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(done))!);
    File.WriteAllText(done, JsonSerializer.Serialize(new { version = 1, at = DateTime.UtcNow }));
}
Log("done");
return 0;

static class Args
{
    public static Dictionary<string, string> Parse(string[] a)
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i + 1 < a.Length; i += 2) d[a[i].TrimStart('-')] = a[i + 1];
        return d;
    }
}
