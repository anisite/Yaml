# YamlD

> A French version is available in the source repository (`README.fr.md`).

A native, high-performance **YAML 1.2.2** parser for **.NET 10**, with the **`<<` merge keys** of YAML 1.1.
Designed after `System.Text.Json`:

| System.Text.Json | YamlD |
|---|---|
| `Utf8JsonReader` (ref struct, pull parser, `ReadOnlySpan<byte>`) | `Utf8YamlReader` |
| `JsonDocument` / `JsonElement` (compact metadata database, pooled buffers) | `YamlDocument` / `YamlElement` |
| `JsonReaderOptions` | `YamlReaderOptions`, `YamlDocumentOptions` |
| `JsonException` | `YamlException` (line / column / offset) |
| `JsonSerializer` | `YamlSerializer` (deserialization to objects) |

YamlD is independent of YamlDotNet; it only shares the YAML language with it.

## Installation

```
dotnet add package YamlD
```

Target framework: **net10.0**. Current version: **0.0.1** (first preview, the API may still change).

## Usage

```csharp
// Pull reader: no allocation, scalars are returned zero-copy when no decoding is needed
var reader = new Utf8YamlReader(utf8Bytes);
try
{
    while (reader.Read())
    {
        switch (reader.TokenType)
        {
            case YamlTokenType.Scalar:
                ReadOnlySpan<byte> value = reader.ValueSpan;   // decoded (folding, escapes, block scalars)
                if (reader.HasAnchor) { /* reader.Anchor */ }
                string? tag = reader.GetTag();                 // full URI, %TAG directives applied
                break;
            case YamlTokenType.StartMapping: /* reader.CollectionStyle, reader.Anchor ... */ break;
        }
    }
}
finally { reader.Dispose(); }            // returns the pooled buffers

// DOM
using YamlDocument doc = YamlDocument.Parse(File.ReadAllBytes("config.yaml"));
YamlElement root = doc.RootElement;
int port = root["server"]["port"].GetInt32();
foreach (var (key, value) in root["services"].EnumerateMapping()) { /* <<: *defaults already merged */ }
string json = root.ToJsonString();

// Multi-document streams, Stream input, UTF-16/32
List<YamlDocument> docs = YamlDocument.ParseAll(stream);
```

Useful options: `MaxDepth`, `SkipInputValidation`, `MergeKeysDisabled`, `DuplicateKeys` (`Error` by default, as the
spec requires), `Schema` (`Core` by default, `Json`, `Failsafe`, `Yaml11`).

## Specification coverage

* **Official yaml-test-suite** (tag `data-2022-01-17`, 402 cases, including every example of the 1.2.2 spec):
  * the event stream is identical to `test.event` for all 307 valid cases;
  * all 95 invalid cases are rejected with a `YamlException`;
  * for the 279 cases that have an `in.json`, the DOM-to-JSON conversion is identical.
* UTF-8 / UTF-16 / UTF-32 (BOM or null-byte pattern detection), UTF-8 and `c-printable` validation.
* Multiple documents, `---`, `...`, `%YAML` (any 1.x version) and `%TAG` directives, reserved directives ignored.
* Block and flow styles, explicit `?` keys, complex keys, single-pair mappings in flow sequences,
  adjacent JSON-like keys (`{"a":1}`), multi-line keys in flow mappings.
* Plain / `'...'` / `"..."` scalars (all escapes, surrogate pairs), `|` and `>` with indentation and chomping
  indicators (`-`, `+`), line folding.
* Anchors, aliases, verbatim / shorthand / non-specific tags, empty properties.
* Failsafe, JSON and core schemas (`null`, `bool`, `int` dec/`0o`/`0x`, `float`, `.inf`, `.nan`), explicit tags
  (`!!str`, `!!int`, ...), and a `Yaml11` option (`yes/no/on/off`, legacy octal, `_`, sexagesimal).
* **`<<` merge keys**: the value is a mapping, an alias to a mapping, or a sequence of mappings. Explicit keys win,
  then the sources in sequence order (the first one wins), merged recursively. Only the untagged plain key `<<`
  (or `!!merge`) is a merge; a quoted `"<<"` stays an ordinary key. Resolution is lazy (nothing is copied, so there is
  no memory blow-up); `Count`, `TryGetProperty` and `EnumerateMapping` see the merged result.
* Robustness: mutation fuzzing (about 2 million parses) with no exception other than `YamlException` and no hang;
  depth limit; the JSON expansion of aliases is bounded (`maxNodes`).

## Design choices and known limits

* The whole input is held in memory (no partial reads like `Utf8JsonReader` / `ReadOnlySequence`).
* No YAML writer: read-only. The deserializer (`YamlSerializer`) is described below.
* Only LF, CR and CRLF are line breaks (YAML 1.2); NEL / LS / PS are content.
* The reader does not require an alias to point to a defined anchor; the DOM does, and it also rejects recursive
  aliases (anchor still open).
* Duplicate key detection compares the decoded text and the resolved type (no numeric canonical form:
  `1` and `0x1` are distinct).
* A block scalar whose last line consists only of spaces at the very end of the stream counts as a line
  (this is what the official test suite expects).
* `Utf8YamlReader` is a mutable struct: pass it by `ref` and do not copy it while reading.

## Deserialization to objects (`YamlSerializer`)

```csharp
using YamlD.Serialization;

var options = new YamlSerializerOptions
{
    PropertyNamingPolicy = YamlNamingPolicy.CamelCase,   // like YamlDotNet's CamelCaseNamingConvention
    MergeKeys = false,                                   // `<<` is treated as an ordinary key
    DuplicateKeys = YamlDuplicateKeyHandling.Allow,      // the last key wins
};
MyConfig? cfg = YamlSerializer.Deserialize<MyConfig>(File.ReadAllBytes("config.yml"), options);
```

As with `System.Text.Json`: create the options once and reuse them (thread-safe, metadata cached, read-only after
first use); `System.Text.Json` attributes (`[JsonPropertyName]`, `[JsonIgnore]`, `[JsonConverter]`) apply, and source
generation is supported (`options.CreateJsonSerializerOptions()` for the context, then the `JsonTypeInfo<T>` overloads).

**How it works**: the YAML is parsed by `YamlDocument`, written as UTF-8 JSON into a buffer rented from `ArrayPool`
(one `Utf8JsonWriter` reused per thread), then bound by `JsonSerializer`. **Every scalar is written as a JSON string**
(`01` stays `"01"` for a `string` member); YAML converters read the value types: booleans `true/yes/on/y`
(case-insensitive), integers `0x1F` `0b101` `010` (octal) `1_000` `+5`, floats `.inf` `.5`, dates, enums (case-insensitive
name or number), `Guid`. A null scalar (`~`, `null`, empty) yields `default`. An `object` member receives `string`,
`List<object>` and `Dictionary<object, object>` (`UntypedMappingType = StringKeys` for `Dictionary<string, object>`),
like YamlDotNet; `Dictionary<object, TValue>` is supported.

* `TypeInfoModifiers`: a hook equivalent to `DefaultJsonTypeInfoResolver.Modifiers` (rename or remove members without
  touching the model).
* `HexByteArrays`: `byte[]` read from a hexadecimal string. `TagMappings`: a tagged mapping instantiates a given type
  (where the target is `object`).
* Binding errors are `YamlException`s carrying the member path and the YAML line/column.
* Limits: a mapping key that is a collection (`? [a, b]`) is bound under its description (`Mapping[1]`,
  `Sequence[2]`); tags on scalars are not interpreted; a mapped tag is only applied on an `object` target.

## Performance (measured with BenchmarkDotNet, .NET 10, **Windows 11 ARM64**)

**Event reader** (mean time, lower is better; the factor is relative to YamlD):

| Dataset | YamlD | VYaml 1.4 | YamlDotNet 16.3 |
|---|--:|--:|--:|
| config (anchors, merge, block scalars) | **1,204 µs** | 2,513 µs (x2.1) | 10,866 µs (x9.0) |
| flow (JSON-like) | **7,546 µs** | 15,250 µs (x2.0) | 56,090 µs (x7.4) |
| k8s (multi-document) | **845 µs** | 1,780 µs (x2.1) | 7,893 µs (x9.3) |
| prose (large scalars) | **657 µs** | 2,120 µs (x3.2) | 4,632 µs (x7.1) |
| records (sequence of mappings) | **6,988 µs** | 14,502 µs (x2.1) | 76,552 µs (x11.0) |

Memory allocated per pass on the same datasets:

| Dataset | YamlD | VYaml | YamlDotNet |
|---|--:|--:|--:|
| config | 3 B | 385,656 B | 9,873,940 B |
| flow | 162 B | 17,128 B | 61,639,512 B |
| k8s | 2 B | 1,136 B | 7,965,231 B |
| prose | 3 B | 1,136 B | 2,742,209 B |
| records | 11 B | 1,136 B | 66,723,040 B |

**DOM**: VYaml has no DOM, so the comparison is against YamlDotNet's `YamlStream.Load`
(`YamlDocument.Parse` with duplicate key detection enabled):

| Dataset | YamlD | YamlDotNet | Memory: YamlD -> YamlDotNet |
|---|--:|--:|--:|
| config | **1.45 ms** | 13.19 ms (x9.1) | 3.6 KB -> 12.4 MB |
| k8s (`ParseAll`, 400 documents) | **2.34 ms** | 11.12 ms (x4.8) | 360 KB -> 11.4 MB |
| records | **10.5 ms** | 103.2 ms (x9.8) | 64 KB -> 94.2 MB |

Duplicate-key detection used to cost about 40% of the DOM time on `records`; it was optimized after this
measurement (lengths are compared before bytes), so the DOM is faster today than these figures
(`--quick`: 179 MB/s on `records`, against 209 MB/s for the reader alone). The DOM lines were not re-measured with
BenchmarkDotNet.

These figures only hold for this machine and these synthetic datasets; rerun them on your hardware (x64 in particular,
not measured here). They do not prove that this is "the fastest in the world", only that it is faster than VYaml and
YamlDotNet in these scenarios.

## Build, test and benchmark

```powershell
dotnet test --project tests/YamlD.Tests -c Release      # TUnit: official suite + unit tests + fuzz
dotnet run -c Release --project tools/SuiteRunner -- tests/YamlD.Tests/suite          # conformance report
dotnet run -c Release --project tools/SuiteRunner -- fuzz tests/YamlD.Tests/suite 400 1 # long fuzz run
cd bench/YamlD.Benchmarks
dotnet run -c Release -- --check                                  # checks that the 3 parsers see the same content
dotnet run -c Release -- --quick                                  # quick comparison (MB/s)
dotnet run -c Release -- --filter *ParseEvents*                   # full BenchmarkDotNet run
dotnet pack src/YamlD -c Release                                  # builds the NuGet package into artifacts/
```

## License

MIT (the `LICENSE` file is shipped in the package). Third-party notices, including libyaml, are in the `NOTICE` file. The test data comes from
[yaml-test-suite](https://github.com/yaml/yaml-test-suite) (MIT, see `tests/YamlD.Tests/suite/LICENSE`).
