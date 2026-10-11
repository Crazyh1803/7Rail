using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using Newtonsoft.Json.Linq;

namespace SevenRail.Convert;

/// Reads each everrail_rules row's source_field out of its EverRail table and writes rules.json { id: number }.
static class RulesExport
{
    public static void Run(DefaultFileProvider provider, string outDir, Action<string> log)
    {
        var tables = new Dictionary<string, JArray>();
        var rules = new SortedDictionary<string, double>();
        foreach (var rule in EverrailRulesRow.All)
        {
            var asset = EverrailAssetsRow.All.First(a => a.Id == rule.SourceTable);
            if (!tables.TryGetValue(asset.Id, out var exports))
                tables[asset.Id] = exports = JArray.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(provider.LoadPackage(asset.Path + ".uasset").GetExports()));
            rules[rule.Id] = Read(exports, rule.SourceField) ?? throw new InvalidDataException($"{rule.Id}: '{rule.SourceField}' not found in {asset.Path}");
            log($"rule {rule.Id} = {rules[rule.Id].ToString(CultureInfo.InvariantCulture)} (seen in the Demo: {rule.SeenValue})");
        }
        File.WriteAllText(Path.Combine(outDir, "rules.json"), JsonSerializer.Serialize(rules, new JsonSerializerOptions { WriteIndented = true }));
    }

    static double? Read(JArray exports, string field)
    {
        // Rows.<Row>.<Prop>[*<Prop2>]  (DataTable)
        var m = Regex.Match(field, @"^Rows\.(\w+)\.(\w+)(?:\*(\w+))?$");
        if (m.Success)
        {
            var row = exports.Select(e => e["Rows"]?[m.Groups[1].Value]).FirstOrDefault(r => r != null);
            if (row == null) return null;
            var v = row[m.Groups[2].Value]?.Value<double>();
            if (v != null && m.Groups[3].Success) v *= row[m.Groups[3].Value]?.Value<double>() ?? double.NaN;
            return v;
        }
        // ItemTrait_Attributes 'Attribute Name'.Value
        m = Regex.Match(field, @"^(\w+) '([^']+)'\.(\w+)$");
        if (m.Success)
        {
            foreach (var e in exports.Where(e => (string?)e["Type"] == m.Groups[1].Value))
                foreach (var tok in e.SelectTokens("$..AttributeName"))
                    if ((string?)tok == m.Groups[2].Value)
                        return tok.Parent!.Parent![m.Groups[3].Value]?.Value<double>();
            return null;
        }
        // plain property on any export
        foreach (var e in exports)
            if (e["Properties"]?[field] is JValue v) return v.Value<double>();
            else if (e[field] is JValue v2) return v2.Value<double>();
        return null;
    }
}
