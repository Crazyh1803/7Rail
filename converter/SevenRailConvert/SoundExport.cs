using System.Diagnostics;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse_Conversion.Sounds;

namespace SevenRail.Convert;

/// EverRail's SoundWaves are Bink Audio ("UEBA"); vgmstream (bundled) turns them into 16-bit WAV for Unity.
static class SoundExport
{
    public static void Run(DefaultFileProvider provider, EverrailAssetsRow row, string outDir, Action<string> log)
    {
        var wave = provider.LoadPackage(row.Path + ".uasset").GetExports().OfType<USoundWave>().FirstOrDefault()
                   ?? throw new InvalidDataException("no SoundWave in package");
        wave.Decode(true, out var format, out var data);
        if (data == null) throw new InvalidDataException($"could not read audio ({format})");
        var wav = Path.Combine(outDir, row.Out);
        Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
        if (format.Equals("WAV", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllBytes(wav, data);
        }
        else
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"7rail-{row.Id}.{format.ToLowerInvariant()}");
            File.WriteAllBytes(tmp, data);
            var psi = new ProcessStartInfo(VgmPath(), $"-o \"{wav}\" \"{tmp}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            File.Delete(tmp);
            if (p.ExitCode != 0 || !File.Exists(wav)) throw new InvalidDataException($"vgmstream failed ({format}): {err.Trim()}");
        }
        log($"sound {row.Id}: {format} -> {row.Out} ({new FileInfo(wav).Length} bytes)");

        string VgmPath() => File.Exists(Vgm) ? Vgm : throw new FileNotFoundException("vgmstream-cli.exe missing next to the converter");
    }

    static string Vgm => Path.Combine(AppContext.BaseDirectory, "vgmstream", "vgmstream-cli.exe");
}
