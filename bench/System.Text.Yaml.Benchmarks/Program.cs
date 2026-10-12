using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using VYaml.Parser;

namespace System.Text.Yaml.Benchmarks;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--check")
        {
            return Check();
        }
        if (args.Length > 1 && args[0] == "--loop")
        {
            byte[] data = args[1] switch
            {
                "k8s" => Datasets.Kubernetes(200),
                "records" => Datasets.Records(5_000),
                "config" => Datasets.Config(1_000),
                "flow" => Datasets.FlowJson(8_000),
                _ => Datasets.Prose(300),
            };
            var end = DateTime.UtcNow.AddSeconds(args.Length > 2 ? int.Parse(args[2]) : 10);
            long sink = 0;
            while (DateTime.UtcNow < end) sink += Contenders.Ours(data, validate: true);
            Console.WriteLine(sink);
            return 0;
        }
        if (args.Length > 1 && args[0] == "--compare")
        {
            return Compare.Run(args[1], args.Contains("-v"));
        }
        if (args.Length > 0 && args[0] == "--lines")
        {
            return LargeFile(args.Length > 1 ? int.Parse(args[1]) : 50_000);
        }
        if (args.Length > 0 && args[0] == "--quick")
        {
            Quick(args.Skip(1).ToArray());
            return 0;
        }

        var config = DefaultConfig.Instance
            .HideColumns("Job", "Error", "StdDev", "RatioSD", "Median");
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
        return 0;
    }

    /// <summary>Builds a YAML file of about <paramref name="lines"/> lines, checks the parsed content, then times every parser.</summary>
    private static int LargeFile(int lines)
    {
        int records = lines / 13; // Datasets.Records emits 13 lines per record
        byte[] data = Datasets.Records(records);
        int actualLines = data.Count(b => b == (byte)'\n');
        Console.WriteLine($"{actualLines:N0} lines, {data.Length:N0} bytes, {records:N0} records");

        // ---- correctness ----
        using (var doc = YamlDocument.Parse(data))
        {
            YamlElement root = doc.RootElement;
            bool ok = root.Count == records;
            foreach (int i in new[] { 0, records / 2, records - 1 })
            {
                YamlElement r = root[i];
                ok &= r["id"].GetInt32() == i;
                ok &= r["name"].GetString() == $"Customer {i}";
                ok &= r["email"].GetString() == $"user{i}@example.com";
                ok &= r["active"].GetBoolean() == (i % 2 == 0);
                ok &= r["tags"].Count == 3 && r["tags"][2].GetString() == $"gamma{i % 7}";
                ok &= r["address"]["street"].GetString() == $"{i} Main Street";
                ok &= r["address"]["zip"].GetString() == (10000 + i % 90000).ToString();
                ok &= r["history"].Count == 2 && r["history"][1]["amount"].GetInt32() == i % 300;
                ok &= r["notes"].IsNull;
            }
            long ids = 0;
            foreach (YamlElement r in root.EnumerateSequence()) ids += r["id"].GetInt64();
            ok &= ids == (long)records * (records - 1) / 2;
            Console.WriteLine($"DOM content check: {(ok ? "OK" : "FAILED")} (root count {root.Count}, sum of ids {ids:N0})");
            if (!ok) return 1;
        }
        long ours = Contenders.Ours(data, validate: true), vy = Contenders.VYamlParse(data), yd = Contenders.YamlDotNetEvents(data);
        Console.WriteLine($"event check: ours={ours:N0} vyaml={vy:N0} yamldotnet={yd:N0} -> {(ours == vy && vy == yd ? "all identical" : "MISMATCH")}");

        // ---- timing ----
        string text = Encoding.UTF8.GetString(data);
        (string, Action)[] runs =
        [
            ("System.Text.Yaml reader", () => Contenders.Ours(data, validate: true)),
            ("VYaml YamlParser", () => Contenders.VYamlParse(data)),
            ("YamlDotNet Parser", () => Contenders.YamlDotNetEvents(data)),
            ("System.Text.Yaml DOM", () => { using var d = YamlDocument.Parse(data); }),
            ("YamlDotNet YamlStream", () => new YamlDotNet.RepresentationModel.YamlStream().Load(new StringReader(text))),
        ];
        Console.WriteLine($"{"parser",-26} {"median",10} {"MB/s",8} {"alloc/run",14}");
        foreach ((string name, Action run) in runs)
        {
            var warm = System.Diagnostics.Stopwatch.StartNew();
            while (warm.ElapsedMilliseconds < 1500) run(); // let tiered JIT / PGO settle before measuring
            var times = new List<double>();
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 15; i++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                run();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            long alloc = (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / 15;
            times.Sort();
            double med = times[times.Count / 2];
            Console.WriteLine($"{name,-26} {med,8:F2} ms {data.Length / 1e6 / (med / 1000),8:F0} {alloc,12:N0} B");
        }
        return 0;
    }

    /// <summary>Fast stopwatch-based throughput comparison used while tuning (BenchmarkDotNet is the reference).</summary>
    private static void Quick(string[] names)
    {
        var all = new Dictionary<string, byte[]>
        {
            ["k8s"] = Datasets.Kubernetes(200),
            ["records"] = Datasets.Records(5_000),
            ["config"] = Datasets.Config(1_000),
            ["flow"] = Datasets.FlowJson(8_000),
            ["prose"] = Datasets.Prose(300),
        };
        foreach ((string name, byte[] data) in all)
        {
            if (names.Length > 0 && !names.Contains(name)) continue;
            double v = Measure(() => Contenders.VYamlParse(data), data.Length);
            double o = Measure(() => Contenders.Ours(data, validate: true), data.Length);
            double d = Measure(() => { foreach (var doc in YamlDocument.ParseAll(data)) doc.Dispose(); }, data.Length);
            Console.WriteLine($"{name,-8} vyaml {v,7:F0} MB/s   ours {o,7:F0} MB/s  ({o / v:F2}x)   dom {d,7:F0} MB/s");
        }
    }

    private static double Measure(Action a, int bytes)
    {
        var end = DateTime.UtcNow.AddMilliseconds(600);
        while (DateTime.UtcNow < end) a();
        double best = 0;
        for (int round = 0; round < 5; round++)
        {
            int iters = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 300) { a(); iters++; }
            double mbps = (double)bytes * iters / sw.Elapsed.TotalSeconds / 1e6;
            if (mbps > best) best = mbps;
        }
        return best;
    }

    /// <summary>Makes sure all contenders see the same document before comparing speed.</summary>
    private static int Check()
    {
        var sets = new (string, byte[])[]
        {
            ("k8s", Datasets.Kubernetes(50)),
            ("records", Datasets.Records(500)),
            ("config", Datasets.Config(100)),
            ("flow", Datasets.FlowJson(500)),
            ("prose", Datasets.Prose(50)),
        };
        int bad = 0;
        foreach ((string name, byte[] data) in sets)
        {
            long ours = Contenders.Ours(data, validate: true);
            long theirs = Contenders.VYamlParse(data);
            long yamlDotNet = Contenders.YamlDotNetEvents(data);
            Console.WriteLine($"{name,-8} {data.Length,9:N0} B   ours={ours,10:N0}  vyaml={theirs,10:N0}  yamldotnet={yamlDotNet,10:N0}");
            if (ours != theirs) bad++;
        }
        return bad == 0 ? 0 : 1;
    }
}

public static class Contenders
{
    /// <summary>Number of events plus total scalar bytes (cheap to compute, defeats dead-code elimination).</summary>
    public static long Ours(byte[] data, bool validate)
    {
        var r = new Utf8YamlReader(data, new YamlReaderOptions { SkipInputValidation = !validate });
        long n = 0;
        try
        {
            while (r.Read())
            {
                n++;
                if (r.TokenType == YamlTokenType.Scalar) n += r.ValueSpan.Length;
            }
        }
        finally { r.Dispose(); }
        return n;
    }

    public static long VYamlParse(byte[] data)
    {
        var p = YamlParser.FromBytes(data);
        long n = 0;
        while (p.Read())
        {
            var t = p.CurrentEventType;
            if (t == ParseEventType.StreamStart || t == ParseEventType.StreamEnd) continue;
            n++;
            if (t == ParseEventType.Scalar && p.TryGetScalarAsSpan(out var span)) n += span.Length;
        }
        return n;
    }

    public static long YamlDotNetEvents(byte[] data)
    {
        string text = Encoding.UTF8.GetString(data);
        var parser = new YamlDotNet.Core.Parser(new StringReader(text));
        long n = 0;
        while (parser.MoveNext())
        {
            var e = parser.Current;
            if (e is YamlDotNet.Core.Events.StreamStart or YamlDotNet.Core.Events.StreamEnd) continue;
            n++;
            if (e is YamlDotNet.Core.Events.Scalar s) n += Encoding.UTF8.GetByteCount(s.Value);
        }
        return n;
    }
}

[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ParseEvents
{
    private byte[] _data = [];

    [Params("k8s", "records", "config", "flow", "prose")]
    public string Dataset { get; set; } = "k8s";

    [GlobalSetup]
    public void Setup()
    {
        _data = Dataset switch
        {
            "k8s" => Datasets.Kubernetes(200),
            "records" => Datasets.Records(5_000),
            "config" => Datasets.Config(1_000),
            "flow" => Datasets.FlowJson(8_000),
            _ => Datasets.Prose(300),
        };
    }

    [Benchmark(Baseline = true, Description = "VYaml YamlParser")]
    public long VYaml() => Contenders.VYamlParse(_data);

    [Benchmark(Description = "System.Text.Yaml reader")]
    public long Ours() => Contenders.Ours(_data, validate: true);

    [Benchmark(Description = "System.Text.Yaml reader (no validation)")]
    public long OursNoValidation() => Contenders.Ours(_data, validate: false);

    [Benchmark(Description = "YamlDotNet Parser")]
    public long YamlDotNet() => Contenders.YamlDotNetEvents(_data);
}

[MemoryDiagnoser]
public class BuildDocument
{
    private byte[] _data = [];
    private string _text = "";

    [Params("k8s", "records", "config")]
    public string Dataset { get; set; } = "k8s";

    [GlobalSetup]
    public void Setup()
    {
        _data = Dataset switch
        {
            "k8s" => Datasets.Kubernetes(200),
            "records" => Datasets.Records(5_000),
            _ => Datasets.Config(1_000),
        };
        _text = Encoding.UTF8.GetString(_data);
    }

    [Benchmark(Description = "YamlDocument.Parse (single doc / ParseAll)")]
    public int Ours()
    {
        if (Dataset == "k8s")
        {
            var docs = YamlDocument.ParseAll(_data);
            int c = docs.Count;
            foreach (var d in docs) d.Dispose();
            return c;
        }
        using var doc = YamlDocument.Parse(_data);
        return doc.RootElement.Count;
    }

    [Benchmark(Description = "YamlDocument.Parse, duplicate keys allowed")]
    public int OursNoDupCheck()
    {
        var o = new YamlDocumentOptions { DuplicateKeys = YamlDuplicateKeyHandling.Allow };
        if (Dataset == "k8s")
        {
            var docs = YamlDocument.ParseAll(_data, o);
            int c = docs.Count;
            foreach (var d in docs) d.Dispose();
            return c;
        }
        using var doc = YamlDocument.Parse(_data, o);
        return doc.RootElement.Count;
    }

    [Benchmark(Baseline = true, Description = "YamlDotNet YamlStream.Load")]
    public int YamlDotNetDom()
    {
        var stream = new YamlDotNet.RepresentationModel.YamlStream();
        stream.Load(new StringReader(_text));
        return stream.Documents.Count;
    }
}
