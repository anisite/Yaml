using System.Text;
using System.Text.Yaml;
using YamlDotNet.Serialization;

namespace System.Text.Yaml.Benchmarks;

/// <summary>
/// Compatibility check: parses every YAML file of a folder with System.Text.Yaml and with YamlDotNet, reduces both
/// results to the same canonical text (scalars as text, null/empty as ∅) and reports rejections and differences.
/// </summary>
public static class Compare
{
    public static int Run(string dir, bool verbose)
    {
        int ok = 0, diff = 0, ourRejects = 0, theirRejects = 0;
        var deserializer = new DeserializerBuilder().Build();

        foreach (string file in Directory.EnumerateFiles(dir, "*.y*ml", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            byte[] raw = File.ReadAllBytes(file);
            foreach (bool bom in new[] { false, true })
            {
                byte[] bytes = bom ? [0xEF, 0xBB, 0xBF, .. raw] : raw;
                string label = Path.GetRelativePath(dir, file) + (bom ? " [+BOM]" : "");

                string? ours = null, oursErr = null, theirs = null, theirsErr = null;
                try
                {
                    using var doc = YamlDocument.Parse(bytes);
                    ours = Canon(doc.RootElement);
                }
                catch (YamlException e) { oursErr = e.Message; }

                try
                {
                    // Like StreamReader / File.ReadAllText: a leading UTF-8 BOM is consumed, not part of the text.
                    string text = bom ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) : Encoding.UTF8.GetString(bytes);
                    object? o = deserializer.Deserialize<object>(text);
                    theirs = Canon(o);
                }
                catch (Exception e) { theirsErr = e.GetType().Name + ": " + e.Message; }

                if (oursErr is not null) ourRejects++;
                if (theirsErr is not null) theirRejects++;

                if (oursErr is null && theirsErr is null)
                {
                    if (ours == theirs) { ok++; if (verbose) Console.WriteLine($"OK    {label}"); }
                    else
                    {
                        diff++;
                        Console.WriteLine($"DIFF  {label}");
                        int i = 0;
                        while (i < ours!.Length && i < theirs!.Length && ours[i] == theirs[i]) i++;
                        Console.WriteLine($"   ours  : ...{Excerpt(ours, i)}");
                        Console.WriteLine($"   theirs: ...{Excerpt(theirs!, i)}");
                    }
                }
                else
                {
                    Console.WriteLine($"REJECT {label}: ours={(oursErr ?? "accepted")} | YamlDotNet={(theirsErr ?? "accepted")}");
                }
            }
        }
        Console.WriteLine($"identical={ok} different={diff} rejected-by-ours={ourRejects} rejected-by-YamlDotNet={theirRejects}");
        return diff + ourRejects == 0 ? 0 : 1;
    }

    private static string Excerpt(string s, int at) => s.Substring(Math.Max(0, at - 30), Math.Min(80, s.Length - Math.Max(0, at - 30)));

    private static string Canon(YamlElement e)
    {
        switch (e.ValueKind)
        {
            case YamlValueKind.Mapping:
                return "{" + string.Join(";", e.EnumerateMapping().Select(kv => Canon(kv.Key) + "=" + Canon(kv.Value))) + "}";
            case YamlValueKind.Sequence:
                return "[" + string.Join(";", e.EnumerateSequence().Select(Canon)) + "]";
            case YamlValueKind.Null:
                return "∅";
            default:
                return "'" + e.ToString() + "'";
        }
    }

    private static string Canon(object? o)
    {
        switch (o)
        {
            case null:
                return "∅";
            case string s:
                return s.Length == 0 || s is "null" or "~" or "Null" or "NULL" ? "∅" : "'" + s + "'";
            case System.Collections.IDictionary d:
                var parts = new List<string>();
                foreach (System.Collections.DictionaryEntry kv in d) parts.Add(Canon(kv.Key) + "=" + Canon(kv.Value));
                return "{" + string.Join(";", parts) + "}";
            case System.Collections.IEnumerable list:
                var items = new List<string>();
                foreach (object? x in list) items.Add(Canon(x));
                return "[" + string.Join(";", items) + "]";
            default:
                return "'" + Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) + "'";
        }
    }
}
