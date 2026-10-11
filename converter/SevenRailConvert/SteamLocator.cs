using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SevenRail.Convert;

/// Finds EverRail through Steam's library folders, or accepts a folder Melty already found.
static class SteamLocator
{
    static readonly string[] InstallDirs = { "EverRail Demo", "EverRail" };
    public static readonly List<string> Searched = new();

    /// Accepts the game folder, its Content/Paks folder, or a Melty own-copy folder holding EverRail/Content/Paks.
    public static string? PaksDir(string folder)
    {
        foreach (var candidate in new[] { folder, Path.Combine(folder, "EverRail", "Content", "Paks"), Path.Combine(folder, "Content", "Paks") })
            if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "*.utoc").Length > 0) return candidate;
        return null;
    }

    public static string? FindEverRail()
    {
        foreach (var lib in SteamLibraries())
            foreach (var dir in InstallDirs)
            {
                var p = Path.Combine(lib, "steamapps", "common", dir);
                Searched.Add(p);
                if (PaksDir(p) != null) return p;
            }
        return null;
    }

    static IEnumerable<string> SteamLibraries()
    {
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                    ?? @"C:\Program Files (x86)\Steam";
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        var libs = new List<string> { steam };
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        return libs.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
