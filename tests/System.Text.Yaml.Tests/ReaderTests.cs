using System.Diagnostics;

namespace System.Text.Yaml.Tests;

public class ReaderTests
{
    private static string Events(string yaml) => EventDump.Dump(Encoding.UTF8.GetBytes(yaml));

    [Test]
    public async Task ScalarsAreZeroCopyWhenNoDecodingIsNeeded()
    {
        (bool zeroCopy, bool decoded) = Probe();
        await Assert.That(zeroCopy).IsTrue();
        await Assert.That(decoded).IsTrue();
    }

    private static (bool, bool) Probe()
    {
        ReadOnlySpan<byte> yaml = "plain: simple\nfolded: a\n  b\n"u8;
        var r = new Utf8YamlReader(yaml);
        try
        {
            bool zeroCopy = false, decoded = false;
            while (r.Read())
            {
                if (r.TokenType != YamlTokenType.Scalar) continue;
                if (r.ValueEquals("simple"u8)) zeroCopy = r.ValueIsInInput;
                if (r.ValueEquals("a b"u8)) decoded = !r.ValueIsInInput;
            }
            return (zeroCopy, decoded);
        }
        finally { r.Dispose(); }
    }

    [Test]
    public async Task DepthAndSkip()
    {
        await Assert.That(RunDepthAndSkip()).IsEqualTo("a@1 seq@1 after@1:EndSequence d@1 e@1");
    }

    private static string RunDepthAndSkip()
    {
        var r = new Utf8YamlReader("a: [1, {b: c}, 2]\nd: e\n"u8);
        try
        {
            var seen = new List<string>();
            while (r.Read())
            {
                if (r.TokenType == YamlTokenType.StartSequence)
                {
                    seen.Add("seq@" + r.CurrentDepth);
                    r.Skip();
                    seen.Add("after@" + r.CurrentDepth + ":" + r.TokenType);
                }
                else if (r.TokenType == YamlTokenType.Scalar)
                {
                    seen.Add(r.GetString() + "@" + r.CurrentDepth);
                }
            }
            return string.Join(" ", seen);
        }
        finally { r.Dispose(); }
    }

    [Test]
    public async Task PositionsAreReported()
    {
        (long line, long col) = FindPosition();
        await Assert.That(line).IsEqualTo(3L);
        await Assert.That(col).IsEqualTo(3L);
    }

    private static (long, long) FindPosition()
    {
        var r = new Utf8YamlReader("a: 1\nb:\n  c: 2\n"u8);
        try
        {
            while (r.Read())
            {
                if (r.TokenType == YamlTokenType.Scalar && r.ValueEquals("c"u8)) return (r.LineNumber, r.ColumnNumber);
            }
            return (0, 0);
        }
        finally { r.Dispose(); }
    }

    [Test]
    public async Task ErrorsCarryPositions()
    {
        var e = Check.Throws<YamlException>(() => Events("a: 1\nb: [x, y\nc: 3\n"));
        await Assert.That(e.LineNumber).IsGreaterThan(0L);
        await Assert.That(e.Message.Contains("line")).IsTrue();
    }

    [Test]
    public async Task MaxDepthIsEnforced()
    {
        string deep = new string('[', 40) + new string(']', 40);
        Check.Throws<YamlException>(() =>
        {
            var r = new Utf8YamlReader(Encoding.UTF8.GetBytes(deep), new YamlReaderOptions { MaxDepth = 10 });
            try { while (r.Read()) { } } finally { r.Dispose(); }
        });
        string deepBlock = string.Concat(Enumerable.Range(0, 40).Select(i => new string(' ', i * 2) + "- \n")) ;
        Check.Throws<YamlException>(() =>
        {
            var r = new Utf8YamlReader(Encoding.UTF8.GetBytes(deepBlock), new YamlReaderOptions { MaxDepth = 10 });
            try { while (r.Read()) { } } finally { r.Dispose(); }
        });
        await Task.CompletedTask;
    }

    [Test]
    public async Task VeryDeepNestingWithinTheLimitDoesNotOverflowTheStack()
    {
        string deep = new string('[', 200) + new string(']', 200);
        using var doc = YamlDocument.Parse(deep);
        YamlElement e = doc.RootElement;
        int depth = 0;
        while (e.ValueKind == YamlValueKind.Sequence && e.Count > 0) { e = e[0]; depth++; }
        await Assert.That(depth).IsEqualTo(199);
    }

    [Test]
    public async Task InvalidUtf8AndControlCharactersAreRejected()
    {
        Check.Throws<YamlException>(() => EventDump.Dump([0x61, 0x3A, 0x20, 0xFF, 0x0A]));
        Check.Throws<YamlException>(() => EventDump.Dump("a: \u0001\n"u8));
        Check.Throws<YamlException>(() => EventDump.Dump("a: \u007F\n"u8));
        Check.Throws<YamlException>(() => EventDump.Dump([0x61, 0x3A, 0x20, 0xEF, 0xBF, 0xBE]));
        // Tab, LF, CR, NEL and astral characters are fine.
        await Assert.That(Events("a:\t\U0001F600\u0085\n")).Contains("\U0001F600");
    }

    [Test]
    public async Task LineBreakStylesAreEquivalent()
    {
        string lf = Events("a: 1\nb: |\n  x\n  y\nc: 'p\n  q'\n");
        await Assert.That(Events("a: 1\r\nb: |\r\n  x\r\n  y\r\nc: 'p\r\n  q'\r\n")).IsEqualTo(lf);
        await Assert.That(Events("a: 1\rb: |\r  x\r  y\rc: 'p\r  q'\r")).IsEqualTo(lf);
    }

    [Test]
    public async Task Utf8BomIsSkipped()
    {
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. "a: 1\n"u8];
        await Assert.That(EventDump.Dump(withBom)).IsEqualTo(Events("a: 1\n"));
    }

    [Test]
    [Arguments("utf-16le-bom")]
    [Arguments("utf-16be-bom")]
    [Arguments("utf-16le")]
    [Arguments("utf-16be")]
    [Arguments("utf-32le-bom")]
    [Arguments("utf-32be-bom")]
    [Arguments("utf-32le")]
    [Arguments("utf-32be")]
    public async Task AllYamlEncodingsAreSupported(string kind)
    {
        const string text = "key: caf\u00E9 \U0001F600\nlist: [1, 2]\n";
        Encoding enc = kind switch
        {
            "utf-16le-bom" or "utf-16le" => new UnicodeEncoding(false, false),
            "utf-16be-bom" or "utf-16be" => new UnicodeEncoding(true, false),
            "utf-32le-bom" or "utf-32le" => new UTF32Encoding(false, false),
            _ => new UTF32Encoding(true, false),
        };
        byte[] body = enc.GetBytes(text);
        byte[] data;
        byte[] bom = kind switch
        {
            "utf-16le-bom" => [0xFF, 0xFE],
            "utf-16be-bom" => [0xFE, 0xFF],
            "utf-32le-bom" => [0xFF, 0xFE, 0, 0],
            "utf-32be-bom" => [0, 0, 0xFE, 0xFF],
            _ => [],
        };
        data = [.. bom, .. body];

        using var doc = YamlDocument.Parse(data);
        await Assert.That(doc.RootElement["key"].GetString()).IsEqualTo("caf\u00E9 \U0001F600");
        await Assert.That(doc.RootElement["list"][1].GetInt32()).IsEqualTo(2);

        using var viaStream = YamlDocument.Parse(new MemoryStream(data));
        await Assert.That(viaStream.RootElement["key"].GetString()).IsEqualTo("caf\u00E9 \U0001F600");
    }

    [Test]
    public async Task MalformedUtf16IsAYamlException()
    {
        // BOM + a lone high surrogate; and a NUL in position 1 that looks like UTF-16 without BOM.
        Check.Throws<YamlException>(() => YamlDocument.Parse(new byte[] { 0xFF, 0xFE, 0x00, 0xD8 }));
        Check.Throws<YamlException>(() => YamlDocument.Parse(new byte[] { 0x23, 0x00, 0x0A }));
        await Task.CompletedTask;
    }

    [Test]
    public async Task EscapesInDoubleQuotedScalars()
    {
        using var doc = YamlDocument.Parse("""
            - "\x41\u00e9\U0001F600"
            - "\0\a\b\t\n\v\f\r\e\ \"\/\\\N\_\L\P"
            - "\uD83D\uDE00"
            - "line1\
              \ line2"
            - "trailing  \
              next"
            """);
        await Assert.That(doc.RootElement[0].GetString()).IsEqualTo("A\u00E9\U0001F600");
        await Assert.That(doc.RootElement[1].GetString()).IsEqualTo("\0\a\b\t\n\v\f\r\u001b \"/\\\u0085\u00A0\u2028\u2029");
        await Assert.That(doc.RootElement[2].GetString()).IsEqualTo("\U0001F600");
        await Assert.That(doc.RootElement[3].GetString()).IsEqualTo("line1 line2");
        await Assert.That(doc.RootElement[4].GetString()).IsEqualTo("trailing  next");
        Check.Throws<YamlException>(() => YamlDocument.Parse("\"\\q\""));
        Check.Throws<YamlException>(() => YamlDocument.Parse("\"\\uD800\""));
        Check.Throws<YamlException>(() => YamlDocument.Parse("\"\\x4\""));
    }

    [Test]
    public async Task BlockScalarChomping()
    {
        using var doc = YamlDocument.Parse("""
            - |
              keep one

            - |-
              strip
            - |+
              keep all

            - >
              folded
              text

              para
            - >-
              folded
               more
              back
            """);
        await Assert.That(doc.RootElement[0].GetString()).IsEqualTo("keep one\n");
        await Assert.That(doc.RootElement[1].GetString()).IsEqualTo("strip");
        await Assert.That(doc.RootElement[2].GetString()).IsEqualTo("keep all\n\n");
        await Assert.That(doc.RootElement[3].GetString()).IsEqualTo("folded text\npara\n");
        await Assert.That(doc.RootElement[4].GetString()).IsEqualTo("folded\n more\nback");
    }

    [Test]
    public async Task LargeDocumentRoundTrip()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 20_000; i++)
        {
            sb.Append("- id: ").Append(i).Append("\n  name: \"item ").Append(i).Append("\"\n  tags: [a, b, c]\n  nested:\n    x: 1.5\n    y: ~\n");
        }
        using var doc = YamlDocument.Parse(sb.ToString());
        await Assert.That(doc.RootElement.Count).IsEqualTo(20_000);
        await Assert.That(doc.RootElement[19_999]["name"].GetString()).IsEqualTo("item 19999");
        await Assert.That(doc.RootElement[12345]["nested"]["x"].GetDouble()).IsEqualTo(1.5);
    }
}

public class RobustnessTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "suite");

    /// <summary>
    /// Mutates every input of the official suite and checks that the parser only ever reports
    /// <see cref="YamlException"/>, terminates quickly, and never throws anything else (no crashes on garbage).
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MutatedInputsFailCleanly(bool skipValidation)
    {
        var rng = new Random(12345);
        byte[] alphabet = "-?:,[]{}#&*!|>'\"%@` \t\n\r\\.:abc019~<="u8.ToArray();
        int parsed = 0, rejected = 0;
        var sw = Stopwatch.StartNew();

        foreach (string file in Directory.EnumerateFiles(Root, "in.yaml", SearchOption.AllDirectories))
        {
            byte[] original = File.ReadAllBytes(file);
            for (int m = 0; m < 12; m++)
            {
                byte[] data = Mutate(original, rng, alphabet);
                var one = Stopwatch.StartNew();
                try
                {
                    var r = new Utf8YamlReader(data, new YamlReaderOptions { SkipInputValidation = skipValidation });
                    try { while (r.Read()) { } } finally { r.Dispose(); }
                    parsed++;
                }
                catch (YamlException)
                {
                    rejected++;
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException($"{file}: unexpected {e.GetType().Name}: {e.Message}\n{Convert.ToBase64String(data)}", e);
                }

                try
                {
                    using var doc = YamlDocument.Parse(data, new YamlDocumentOptions { SkipInputValidation = skipValidation });
                    Walk(doc.RootElement, 0);
                }
                catch (YamlException) { }
                catch (InvalidOperationException) { } // e.g. complex keys cannot be converted to JSON
                catch (Exception e)
                {
                    throw new InvalidOperationException($"{file} (DOM): unexpected {e.GetType().Name}: {e.Message}\n{Convert.ToBase64String(data)}", e);
                }

                if (one.ElapsedMilliseconds > 2000)
                {
                    throw new InvalidOperationException($"{file}: parsing took {one.ElapsedMilliseconds} ms\n{Convert.ToBase64String(data)}");
                }
            }
        }

        await Assert.That(parsed + rejected).IsGreaterThan(4000);
        await Assert.That(rejected).IsGreaterThan(0);
    }

    private static void Walk(YamlElement e, int depth)
    {
        switch (e.ValueKind)
        {
            case YamlValueKind.Mapping:
                foreach (YamlEntry kv in e.EnumerateMapping()) { Walk(kv.Key, depth + 1); Walk(kv.Value, depth + 1); }
                break;
            case YamlValueKind.Sequence:
                foreach (YamlElement c in e.EnumerateSequence()) Walk(c, depth + 1);
                break;
            default:
                _ = e.ToString();
                break;
        }
    }

    private static byte[] Mutate(byte[] src, Random rng, byte[] alphabet)
    {
        var list = new List<byte>(src);
        int edits = rng.Next(1, 4);
        for (int i = 0; i < edits && list.Count > 0; i++)
        {
            int pos = rng.Next(list.Count);
            switch (rng.Next(5))
            {
                case 0: list[pos] = alphabet[rng.Next(alphabet.Length)]; break;
                case 1: list.Insert(pos, alphabet[rng.Next(alphabet.Length)]); break;
                case 2: list.RemoveAt(pos); break;
                case 3: list.RemoveRange(pos, list.Count - pos); break;
                default:
                    int len = Math.Min(rng.Next(1, 20), list.Count - pos);
                    list.InsertRange(rng.Next(list.Count), list.GetRange(pos, len));
                    break;
            }
        }
        return list.ToArray();
    }
}
