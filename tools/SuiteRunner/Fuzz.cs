using System.Diagnostics;
using YamlD;

/// <summary>Long-running mutation fuzzer: `SuiteRunner fuzz &lt;suite-dir&gt; &lt;mutations-per-input&gt; &lt;seed&gt;`.</summary>
internal static class Fuzz
{
    public static int Run(string root, int perInput, int seed)
    {
        var rng = new Random(seed);
        byte[] alphabet = "-?:,[]{}#&*!|>'\"%@` \t\n\r\\.:abc019~<=\0\u0001"u8.ToArray();
        int ok = 0, rejected = 0, bugs = 0;
        var sw = Stopwatch.StartNew();
        foreach (string file in Directory.EnumerateFiles(root, "in.yaml", SearchOption.AllDirectories))
        {
            byte[] original = File.ReadAllBytes(file);
            for (int m = 0; m < perInput; m++)
            {
                byte[] data = Mutate(original, rng, alphabet);
                foreach (bool skip in new[] { false, true })
                {
                    var t = Stopwatch.StartNew();
                    try
                    {
                        var r = new Utf8YamlReader(data, new YamlReaderOptions { SkipInputValidation = skip, MaxDepth = 64 });
                        try { while (r.Read()) { } } finally { r.Dispose(); }
                        using var doc = YamlDocument.Parse(data, new YamlDocumentOptions { SkipInputValidation = skip, MaxDepth = 64 });
                        Walk(doc.RootElement);
                        ok++;
                    }
                    catch (YamlException) { rejected++; }
                    catch (InvalidOperationException e) when (e.Message.Contains("non-scalar key") || e.Message.Contains("JSON") || e.Message.Contains("expansion")) { rejected++; }
                    catch (Exception e)
                    {
                        bugs++;
                        Console.WriteLine($"BUG {file} skip={skip}: {e.GetType().Name}: {e.Message}\n{Convert.ToBase64String(data)}\n{e.StackTrace}");
                    }
                    if (t.ElapsedMilliseconds > 1500)
                    {
                        bugs++;
                        Console.WriteLine($"SLOW {file} {t.ElapsedMilliseconds} ms\n{Convert.ToBase64String(data)}");
                    }
                }
            }
        }
        Console.WriteLine($"fuzz: ok={ok} rejected={rejected} bugs={bugs} in {sw.Elapsed.TotalSeconds:F1}s");
        return bugs == 0 ? 0 : 1;
    }

    private static void Walk(YamlElement e)
    {
        switch (e.ValueKind)
        {
            case YamlValueKind.Mapping:
                foreach (YamlEntry kv in e.EnumerateMapping()) { Walk(kv.Key); Walk(kv.Value); }
                break;
            case YamlValueKind.Sequence:
                foreach (YamlElement c in e.EnumerateSequence()) Walk(c);
                break;
            default:
                _ = e.ToString();
                _ = e.TryGetDouble(out _);
                _ = e.TryGetInt64(out _);
                break;
        }
    }

    private static byte[] Mutate(byte[] src, Random rng, byte[] alphabet)
    {
        var list = new List<byte>(src);
        int edits = rng.Next(1, 6);
        for (int i = 0; i < edits && list.Count > 0; i++)
        {
            int pos = rng.Next(list.Count);
            switch (rng.Next(6))
            {
                case 0: list[pos] = alphabet[rng.Next(alphabet.Length)]; break;
                case 1: list.Insert(pos, alphabet[rng.Next(alphabet.Length)]); break;
                case 2: list.RemoveAt(pos); break;
                case 3: list.RemoveRange(pos, list.Count - pos); break;
                case 4: list[pos] = (byte)rng.Next(256); break;
                default:
                    int len = Math.Min(rng.Next(1, 30), list.Count - pos);
                    list.InsertRange(rng.Next(list.Count), list.GetRange(pos, len));
                    break;
            }
        }
        return list.ToArray();
    }
}
