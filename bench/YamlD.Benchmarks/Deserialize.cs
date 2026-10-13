using System.Globalization;
using BenchmarkDotNet.Attributes;
using YamlD.Serialization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace YamlD.Benchmarks;

public sealed class Address
{
    public string Street { get; set; } = "";
    public string City { get; set; } = "";
    public string Zip { get; set; } = "";
}

public sealed class HistoryItem
{
    public string Date { get; set; } = "";
    public int Amount { get; set; }
}

public sealed class Record
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public bool Active { get; set; }
    public double Balance { get; set; }
    public List<string> Tags { get; set; } = [];
    public Address Address { get; set; } = new();
    public List<HistoryItem> History { get; set; } = [];
    public string? Notes { get; set; }
}

/// <summary>Typed deserialization (YAML text to a list of objects) with and without the document cache.</summary>
[MemoryDiagnoser]
public class Deserialize
{
    private byte[] _bytes = [];
    private string _text = "";
    private YamlSerializerOptions _plain = null!;
    private YamlSerializerOptions _cached = null!;
    private IDeserializer _yamlDotNet = null!;

    [Params(1, 100, 3846)]
    public int Records { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _bytes = TypedRecords(Records);
        _text = Encoding.UTF8.GetString(_bytes);
        _plain = new YamlSerializerOptions { PropertyNamingPolicy = YamlNamingPolicy.CamelCase };
        _cached = new YamlSerializerOptions
        {
            PropertyNamingPolicy = YamlNamingPolicy.CamelCase,
            DocumentCache = new YamlDocumentCache(maxEntries: 8, maxBytes: 256L * 1024 * 1024),
        };
        _yamlDotNet = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        // The cache is warmed once so that the measured calls are hits; check that everything agrees.
        int a = YamlSerializer.Deserialize<List<Record>>(_bytes, _plain)!.Count;
        int b = YamlSerializer.Deserialize<List<Record>>(_bytes, _cached)!.Count;
        int c = _yamlDotNet.Deserialize<List<Record>>(_text).Count;
        if (a != Records || b != Records || c != Records) throw new InvalidOperationException($"counts differ: {a} {b} {c} (expected {Records})");
    }

    [Benchmark(Baseline = true, Description = "YamlDotNet Deserializer")]
    public int YamlDotNetDeserializer() => _yamlDotNet.Deserialize<List<Record>>(_text).Count;

    [Benchmark(Description = "YamlD")]
    public int YamlD() => YamlSerializer.Deserialize<List<Record>>(_bytes, _plain)!.Count;

    [Benchmark(Description = "YamlD with cache (same text again)")]
    public int YamlDWithCache() => YamlSerializer.Deserialize<List<Record>>(_bytes, _cached)!.Count;

    /// <summary>Same shape as <see cref="Datasets.Records"/>, but numbers are written with the invariant culture.</summary>
    public static byte[] TypedRecords(int count)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            sb.Append("- id: ").Append(i).Append('\n');
            sb.Append("  name: \"Customer ").Append(i).Append("\"\n");
            sb.Append("  email: user").Append(i).Append("@example.com\n");
            sb.Append("  active: ").Append(i % 2 == 0 ? "true" : "false").Append('\n');
            sb.Append("  balance: ").Append(((i * 37 % 100000) / 100.0).ToString(inv)).Append('\n');
            sb.Append("  tags: [alpha, beta, gamma").Append(i % 7).Append("]\n");
            sb.Append("  address:\n");
            sb.Append("    street: ").Append(i).Append(" Main Street\n");
            sb.Append("    city: Springfield\n");
            sb.Append("    zip: '").Append(10000 + i % 90000).Append("'\n");
            sb.Append("  history:\n");
            sb.Append("    - {date: 2024-01-").Append(1 + i % 28).Append(", amount: ").Append(i % 500).Append("}\n");
            sb.Append("    - {date: 2024-02-").Append(1 + i % 28).Append(", amount: ").Append(i % 300).Append("}\n");
            sb.Append("  notes: ~\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
