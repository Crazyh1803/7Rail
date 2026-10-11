using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;

var dir = args[0];
var provider = new DefaultFileProvider(dir, SearchOption.AllDirectories, new VersionContainer(Enum.Parse<EGame>(Environment.GetEnvironmentVariable("UEVER") ?? "GAME_UE5_6")));
if (Environment.GetEnvironmentVariable("USMAP") is { } um) provider.MappingsContainer = new CUE4Parse.MappingsProvider.Usmap.FileUsmapTypeMappingsProvider(um);
provider.Initialize();
provider.Mount();
var cmd = args[1];
if (cmd == "ls") { foreach (var k in provider.Files.Keys.Where(k => k.StartsWith(args[2], StringComparison.OrdinalIgnoreCase) && !k.EndsWith(".ubulk"))) Console.WriteLine(k); return; }
if (cmd == "full") { File.WriteAllText(args[3], JsonConvert.SerializeObject(provider.LoadPackage(args[2]).GetExports(), Formatting.Indented)); return; }
if (cmd == "decode") { for (int i = 3; i < args.Length; i += 2) { try { Decode.Run(provider, args[i], args[i + 1], args[2]); } catch (Exception e) { Console.WriteLine($"!! {args[i + 1]}: {e.GetType().Name}: {e.Message}"); } } return; }
if (cmd == "json") {
    foreach (var p in args.Skip(2)) {
        try {
            var exports = provider.LoadPackage(p).GetExports().ToList();
            Console.WriteLine($"== {p}: {exports.Count} exports");
            var j = JsonConvert.SerializeObject(exports, Formatting.Indented);
            Console.WriteLine(j.Length > 6000 ? j[..6000] + "\n...[truncated " + j.Length + "]" : j);
        } catch (Exception e) { Console.WriteLine($"!! {p}: {e.GetType().Name}: {e.Message}"); }
    }
}





