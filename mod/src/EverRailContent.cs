using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;

namespace SevenRail
{
    /// EverRail's models, textures, sounds and rules, as the converter wrote them from the player's own
    /// EverRail install into Mods/7Rail/everrail_cache. Nothing here ships with the mod.
    public static class EverRailContent
    {
        public static string CacheDir;
        public static bool Ready;
        public static string Problem;
        static Dictionary<string, double> rules;
        static readonly Dictionary<string, (Mesh mesh, Material[] mats)> meshes = new Dictionary<string, (Mesh, Material[])>();
        static readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
        static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();
        static Shader shader;

        public static void Init(string modPath)
        {
            CacheDir = Path.Combine(modPath, "everrail_cache");
            var rulesFile = Path.Combine(CacheDir, "rules.json");
            if (File.Exists(rulesFile))
            {
                rules = JsonConvert.DeserializeObject<Dictionary<string, double>>(File.ReadAllText(rulesFile));
                var missingRule = EverrailRulesRow.All.FirstOrDefault(r => !rules.ContainsKey(r.Id));
                if (missingRule != null) { Problem = $"EverRail's rules are incomplete ({missingRule.Id}). Press Play in Melty again to re-read EverRail."; Log.Warning("[7Rail] " + Problem); return; }
                Ready = true;
                Log.Out($"[7Rail] EverRail content found in {CacheDir} ({rules.Count} rules)");
                return;
            }
            Problem = File.Exists(Path.Combine(CacheDir, "missing.json"))
                ? "7Rail needs EverRail (the free EverRail Demo on Steam is enough). Install it, then press Play in Melty again: the train comes from your own copy of EverRail."
                : "7Rail hasn't read your EverRail yet. Press Play in Melty to convert it (a few seconds, only once).";
            Log.Warning("[7Rail] " + Problem);
        }

        public static double Rule(EverrailRulesRow r) => rules[r.Id];

        public static AudioClip Sound(EverrailAssetsRow row)
        {
            if (clips.TryGetValue(row.Id, out var c)) return c;
            return clips[row.Id] = Wav.Load(Path.Combine(CacheDir, row.Out), row.Id);
        }

        /// A renderer-ready mesh at EverRail's full size (metres, +Z forward).
        public static (Mesh mesh, Material[] mats) Mesh(EverrailAssetsRow row) => Load(row.Id, Path.Combine(CacheDir, row.Out));

        /// A mesh a layout uses, in EverRail's own frame (meshes/native).
        public static (Mesh mesh, Material[] mats) NativeMesh(string name) => Load("native:" + name, Path.Combine(CacheDir, "meshes", "native", name + ".7rm"));

        static (Mesh mesh, Material[] mats) Load(string key, string file)
        {
            if (meshes.TryGetValue(key, out var m)) return m;
            var dir = Path.GetDirectoryName(file);
            using (var r = new BinaryReader(File.OpenRead(file)))
            {
                if (new string(r.ReadChars(4)) != "7RM2") throw new InvalidDataException(file + " is not a 7Rail mesh (v2); press Play in Melty to re-read EverRail");
                int vc = r.ReadInt32(), sc = r.ReadInt32();
                var pos = new Vector3[vc]; var nrm = new Vector3[vc]; var uv = new Vector2[vc];
                for (int i = 0; i < vc; i++)
                {
                    pos[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    nrm[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    uv[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
                }
                var mesh = new Mesh { name = key, indexFormat = vc > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                mesh.vertices = pos; mesh.normals = nrm; mesh.uv = uv;
                mesh.subMeshCount = sc;
                var mats = new Material[sc];
                for (int s = 0; s < sc; s++)
                {
                    var slot = r.ReadString(); var tex = r.ReadString();
                    var tint = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    var translucent = r.ReadBoolean();
                    var ic = r.ReadInt32(); var idx = new int[ic];
                    for (int i = 0; i < ic; i++) idx[i] = r.ReadInt32();
                    mesh.SetTriangles(idx, s, false);
                    mats[s] = MakeMaterial(slot, tex, tint, dir, translucent);
                }
                mesh.RecalculateBounds();
                return meshes[key] = (mesh, mats);
            }
        }

        static Material MakeMaterial(string slot, string tex, Color tint, string dir, bool translucent)
        {
            var mat = new Material(PickShader()) { name = "7Rail_" + slot };
            mat.color = tint;
            if (tex != "")
            {
                var texKey = Path.Combine(dir, tex);
                if (!textures.TryGetValue(texKey, out var t))
                {
                    t = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                    var png = Path.Combine(dir, tex);
                    if (File.Exists(png)) t.LoadImage(File.ReadAllBytes(png));
                    else Log.Warning("[7Rail] missing texture " + png);
                    t.wrapMode = TextureWrapMode.Repeat;
                    t.anisoLevel = 4;
                    textures[texKey] = t;
                }
                mat.mainTexture = t;
            }
            // EverRail's translucent materials (cab glass, the snow plow's energy shield) stay see-through.
            if (translucent || slot.IndexOf("Glass", StringComparison.OrdinalIgnoreCase) >= 0)
                MakeTransparent(mat, tex == "" ? 0.15f : 0.35f);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.35f);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", slot.IndexOf("Metal", StringComparison.OrdinalIgnoreCase) >= 0 ? 0.6f : 0.2f);
            return mat;
        }

        /// Standard shader in Transparent mode, so EverRail's cab glass can be looked through.
        static void MakeTransparent(Material mat, float alpha)
        {
            var c = mat.color; c.a = alpha; mat.color = c;
            mat.SetFloat("_Mode", 3);
            mat.SetInt("_SrcBlend", (int)BlendMode.One);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHABLEND_ON");
            mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = 3000;
        }

        static Shader PickShader()
        {
            if (shader != null) return shader;
            foreach (var n in new[] { "Standard", "Legacy Shaders/Diffuse", "Diffuse", "Unlit/Texture" })
                if ((shader = Shader.Find(n)) != null) { Log.Out("[7Rail] using shader " + n); return shader; }
            shader = Resources.FindObjectsOfTypeAll<Shader>().FirstOrDefault(s => s.name.Contains("Standard"))
                     ?? Resources.FindObjectsOfTypeAll<Shader>().First();
            Log.Out("[7Rail] using shader " + shader.name);
            return shader;
        }
    }

    /// 16-bit PCM WAV (what the converter writes) into a Unity AudioClip.
    static class Wav
    {
        public static AudioClip Load(string path, string name)
        {
            var b = File.ReadAllBytes(path);
            int channels = 2, rate = 48000, bits = 16, pos = 12;
            while (pos + 8 <= b.Length)
            {
                var id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
                var size = BitConverter.ToInt32(b, pos + 4);
                if (id == "fmt ")
                {
                    channels = BitConverter.ToInt16(b, pos + 10);
                    rate = BitConverter.ToInt32(b, pos + 12);
                    bits = BitConverter.ToInt16(b, pos + 22);
                }
                else if (id == "data")
                {
                    if (bits != 16) throw new InvalidDataException($"{path}: {bits}-bit audio");
                    int n = Math.Min(size, b.Length - pos - 8) / 2;
                    var data = new float[n];
                    for (int i = 0; i < n; i++) data[i] = BitConverter.ToInt16(b, pos + 8 + i * 2) / 32768f;
                    var clip = AudioClip.Create(name, n / channels, channels, rate, false);
                    clip.SetData(data, 0);
                    return clip;
                }
                pos += 8 + size + (size & 1);
            }
            throw new InvalidDataException(path + ": no audio data");
        }
    }
}

