using System.Text.Json.Nodes;

namespace System.Text.Yaml.Tests;

public class MergeKeyTests
{
    // The example from https://yaml.org/type/merge.html (YAML 1.1 merge key language-independent type).
    private const string Spec = """
        ---
        - &CENTER { x: 1, y: 2 }
        - &LEFT { x: 0, y: 2 }
        - &BIG { r: 10 }
        - &SMALL { r: 1 }

        # All the following maps are equal:

        - # Explicit keys
          x: 1
          y: 2
          r: 10
          label: center/big

        - # Merge one map
          << : *CENTER
          r: 10
          label: center/big

        - # Merge multiple maps
          << : [ *CENTER, *BIG ]
          label: center/big

        - # Override
          << : [ *BIG, *LEFT, *SMALL ]
          x: 1
          label: center/big
        """;

    [Test]
    public async Task SpecExample_AllMapsAreEqual()
    {
        using var doc = YamlDocument.Parse(Spec);
        YamlElement root = doc.RootElement;
        await Assert.That(root.Count).IsEqualTo(8);

        for (int i = 4; i < 8; i++)
        {
            YamlElement m = root[i];
            await Assert.That(m["x"].GetInt32()).IsEqualTo(1);
            await Assert.That(m["y"].GetInt32()).IsEqualTo(2);
            await Assert.That(m["r"].GetInt32()).IsEqualTo(10);
            await Assert.That(m["label"].GetString()).IsEqualTo("center/big");
            await Assert.That(m.Count).IsEqualTo(4);
            await Assert.That(m.TryGetProperty("<<", out _)).IsFalse();
        }
    }

    [Test]
    public async Task SpecExample_JsonIsIdentical()
    {
        using var doc = YamlDocument.Parse(Spec);
        string Canon(int i) => string.Join(",", JsonNode.Parse(doc.RootElement[i].ToJsonString())!.AsObject()
            .OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value));
        string reference = Canon(4);
        for (int i = 5; i < 8; i++)
        {
            await Assert.That(Canon(i)).IsEqualTo(reference);
        }
    }

    [Test]
    public async Task ExplicitKeysOverrideMergedOnes()
    {
        using var doc = YamlDocument.Parse("""
            base: &b {a: 1, b: 2}
            derived:
              <<: *b
              b: 20
              c: 3
            """);
        YamlElement d = doc.RootElement["derived"];
        await Assert.That(d["a"].GetInt32()).IsEqualTo(1);
        await Assert.That(d["b"].GetInt32()).IsEqualTo(20);
        await Assert.That(d["c"].GetInt32()).IsEqualTo(3);
        await Assert.That(d.Count).IsEqualTo(3);
    }

    [Test]
    public async Task EarlierMergeSourceWins()
    {
        using var doc = YamlDocument.Parse("""
            - &a {k: first}
            - &b {k: second, only: b}
            - <<: [*a, *b]
            """);
        YamlElement m = doc.RootElement[2];
        await Assert.That(m["k"].GetString()).IsEqualTo("first");
        await Assert.That(m["only"].GetString()).IsEqualTo("b");
        await Assert.That(m.Count).IsEqualTo(2);
    }

    [Test]
    public async Task NestedMerges()
    {
        using var doc = YamlDocument.Parse("""
            a: &a {x: 1}
            b: &b
              <<: *a
              y: 2
            c:
              <<: *b
              z: 3
            """);
        YamlElement c = doc.RootElement["c"];
        await Assert.That(c["x"].GetInt32()).IsEqualTo(1);
        await Assert.That(c["y"].GetInt32()).IsEqualTo(2);
        await Assert.That(c["z"].GetInt32()).IsEqualTo(3);
        string keys = string.Join(",", c.EnumerateMapping().Select(e => e.Key.GetString()!).OrderBy(x => x));
        await Assert.That(keys).IsEqualTo("x,y,z");
    }

    [Test]
    public async Task QuotedMergeKeyIsAnOrdinaryKey()
    {
        using var doc = YamlDocument.Parse("""
            base: &b {a: 1}
            m:
              "<<": *b
            """);
        YamlElement m = doc.RootElement["m"];
        await Assert.That(m.TryGetProperty("a", out _)).IsFalse();
        await Assert.That(m.TryGetProperty("<<", out YamlElement v)).IsTrue();
        await Assert.That(v["a"].GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task MergeCanBeDisabled()
    {
        using var doc = YamlDocument.Parse("""
            base: &b {a: 1}
            m:
              <<: *b
            """, new YamlDocumentOptions { MergeKeysDisabled = true });
        YamlElement m = doc.RootElement["m"];
        await Assert.That(m.TryGetProperty("a", out _)).IsFalse();
        await Assert.That(m.TryGetProperty("<<", out _)).IsTrue();
    }

    [Test]
    [Arguments("m:\n  <<: scalar\n")]
    [Arguments("a: &a [1, 2]\nm:\n  <<: *a\n")]
    [Arguments("m:\n  <<: [1, 2]\n")]
    [Arguments("m:\n  <<: ~\n")]
    public async Task InvalidMergeValueIsRejected(string yaml)
    {
        Check.Throws<YamlException>(() => YamlDocument.Parse(yaml));
        await Task.CompletedTask;
    }

    [Test]
    public async Task MergeOfSequenceOfMappingsViaAlias()
    {
        using var doc = YamlDocument.Parse("""
            sources: &s
              - {a: 1}
              - {b: 2}
            m:
              <<: *s
            """);
        await Assert.That(doc.RootElement["m"]["a"].GetInt32()).IsEqualTo(1);
        await Assert.That(doc.RootElement["m"]["b"].GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task MergeKeyWithExplicitMergeTag()
    {
        using var doc = YamlDocument.Parse("""
            base: &b {a: 1}
            m:
              !!merge "<<": *b
            """);
        await Assert.That(doc.RootElement["m"]["a"].GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task ReaderEmitsMergeKeyAsAnOrdinaryScalar()
    {
        (bool key, bool alias) = ScanForMerge();
        await Assert.That(key).IsTrue();
        await Assert.That(alias).IsTrue();
    }

    private static (bool, bool) ScanForMerge()
    {
        var r = new Utf8YamlReader("a: &x {k: v}\nb:\n  <<: *x\n"u8);
        try
        {
            bool sawKey = false, sawAlias = false;
            while (r.Read())
            {
                if (r.TokenType == YamlTokenType.Scalar && r.ValueEquals("<<"u8)) sawKey = true;
                if (r.TokenType == YamlTokenType.Alias) sawAlias = r.ValueEquals("x"u8);
            }
            return (sawKey, sawAlias);
        }
        finally { r.Dispose(); }
    }
}

public class DocumentTests
{
    [Test]
    public async Task AnchorsAndAliasesResolveToTheSameNode()
    {
        using var doc = YamlDocument.Parse("""
            defaults: &d
              retries: 3
              hosts: [a, b]
            prod: *d
            """);
        YamlElement prod = doc.RootElement["prod"];
        await Assert.That(prod["retries"].GetInt32()).IsEqualTo(3);
        await Assert.That(prod["hosts"][1].GetString()).IsEqualTo("b");
        await Assert.That(doc.RootElement["defaults"].Anchor).IsEqualTo("d");
    }

    [Test]
    public async Task AliasToUndefinedAnchorFails()
    {
        var ex = Check.Throws<YamlException>(() => YamlDocument.Parse("a: *nope"));
        await Assert.That(ex.LineNumber).IsEqualTo(1L);
    }

    [Test]
    public async Task RecursiveAliasFails()
    {
        Check.Throws<YamlException>(() => YamlDocument.Parse("&a [*a]"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task AnchorCanBeRedefined()
    {
        using var doc = YamlDocument.Parse("""
            - &x 1
            - *x
            - &x 2
            - *x
            """);
        string values = string.Join(",", Enumerable.Range(0, 4).Select(i => doc.RootElement[i].GetInt32()));
        await Assert.That(values).IsEqualTo("1,1,2,2");
    }

    [Test]
    public async Task DuplicateKeysAreRejectedByDefault()
    {
        Check.Throws<YamlException>(() => YamlDocument.Parse("a: 1\nb: 2\na: 3\n"));
        Check.Throws<YamlException>(() => YamlDocument.Parse("{a: 1, \"a\": 2}"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task DuplicateKeysOfDifferentTypesAreDistinct()
    {
        using var doc = YamlDocument.Parse("1: int\n\"1\": str\n");
        await Assert.That(doc.RootElement.Count).IsEqualTo(2);
    }

    [Test]
    public async Task DuplicateKeysCanBeAllowedAndLastWins()
    {
        using var doc = YamlDocument.Parse("a: 1\na: 2\n", new YamlDocumentOptions { DuplicateKeys = YamlDuplicateKeyHandling.Allow });
        await Assert.That(doc.RootElement["a"].GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task DuplicateKeysInLargeMappingsAreDetected()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 500; i++) sb.Append("key").Append(i).Append(": ").Append(i).Append('\n');
        string ok = sb.ToString();
        using (YamlDocument.Parse(ok)) { }
        Check.Throws<YamlException>(() => YamlDocument.Parse(ok + "key250: again\n"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task CoreSchemaKinds()
    {
        using var doc = YamlDocument.Parse("""
            - ~
            - null
            - Null
            -
            - true
            - FALSE
            - 42
            - -17
            - +3
            - 0o17
            - 0xFF
            - 3.14
            - 1e3
            - .5
            - .inf
            - -.INF
            - .nan
            - "42"
            - '~'
            - yes
            - 0777
            - 1_000
            - 12:30:45
            - 1.2.3
            - 0x
            - .
            """);
        YamlValueKind[] expected =
        [
            YamlValueKind.Null, YamlValueKind.Null, YamlValueKind.Null, YamlValueKind.Null,
            YamlValueKind.Boolean, YamlValueKind.Boolean,
            YamlValueKind.Integer, YamlValueKind.Integer, YamlValueKind.Integer, YamlValueKind.Integer, YamlValueKind.Integer,
            YamlValueKind.Float, YamlValueKind.Float, YamlValueKind.Float, YamlValueKind.Float, YamlValueKind.Float, YamlValueKind.Float,
            YamlValueKind.String, YamlValueKind.String,
            YamlValueKind.String, // yes: not a boolean in YAML 1.2
            YamlValueKind.Integer, // 0777 is a decimal integer in the 1.2 core schema
            YamlValueKind.String, YamlValueKind.String, YamlValueKind.String, YamlValueKind.String, YamlValueKind.String,
        ];
        for (int i = 0; i < expected.Length; i++)
        {
            await Assert.That(doc.RootElement[i].ValueKind).IsEqualTo(expected[i]);
        }
        await Assert.That(doc.RootElement[20].GetInt32()).IsEqualTo(777);
        await Assert.That(doc.RootElement[9].GetInt32()).IsEqualTo(15);
        await Assert.That(doc.RootElement[10].GetInt32()).IsEqualTo(255);
        await Assert.That(double.IsNaN(doc.RootElement[16].GetDouble())).IsTrue();
        await Assert.That(double.IsNegativeInfinity(doc.RootElement[15].GetDouble())).IsTrue();
        await Assert.That(doc.RootElement[13].GetDouble()).IsEqualTo(0.5);
        await Assert.That(doc.RootElement[0].GetString()).IsNull();
        await Assert.That(doc.RootElement[18].GetString()).IsEqualTo("~");
    }

    [Test]
    public async Task ExplicitTagsOverrideResolution()
    {
        using var doc = YamlDocument.Parse("""
            - !!str 123
            - !!int "42"
            - !!float 1
            - !!bool true
            - !!null ""
            - ! 7
            """);
        await Assert.That(doc.RootElement[0].ValueKind).IsEqualTo(YamlValueKind.String);
        await Assert.That(doc.RootElement[1].ValueKind).IsEqualTo(YamlValueKind.Integer);
        await Assert.That(doc.RootElement[2].ValueKind).IsEqualTo(YamlValueKind.Float);
        await Assert.That(doc.RootElement[3].ValueKind).IsEqualTo(YamlValueKind.Boolean);
        await Assert.That(doc.RootElement[4].ValueKind).IsEqualTo(YamlValueKind.Null);
        await Assert.That(doc.RootElement[5].ValueKind).IsEqualTo(YamlValueKind.String);
        await Assert.That(doc.RootElement[0].Tag).IsEqualTo("tag:yaml.org,2002:str");
    }

    [Test]
    public async Task Yaml11SchemaAcceptsLegacyLiterals()
    {
        using var doc = YamlDocument.Parse("""
            - yes
            - No
            - on
            - 0777
            - 0b101
            - 1_000
            - 1:30
            - 190:20:30.15
            """, new YamlDocumentOptions { Schema = YamlSchema.Yaml11 });
        await Assert.That(doc.RootElement[0].GetBoolean()).IsTrue();
        await Assert.That(doc.RootElement[1].GetBoolean()).IsFalse();
        await Assert.That(doc.RootElement[2].GetBoolean()).IsTrue();
        await Assert.That(doc.RootElement[3].GetInt32()).IsEqualTo(511);
        await Assert.That(doc.RootElement[4].GetInt32()).IsEqualTo(5);
        await Assert.That(doc.RootElement[5].GetInt32()).IsEqualTo(1000);
        await Assert.That(doc.RootElement[6].GetInt32()).IsEqualTo(90);
        await Assert.That(Math.Abs(doc.RootElement[7].GetDouble() - 685230.15) < 1e-6).IsTrue();
    }

    [Test]
    public async Task FailsafeSchemaMakesEverythingAString()
    {
        using var doc = YamlDocument.Parse("[1, true, null]", new YamlDocumentOptions { Schema = YamlSchema.Failsafe });
        for (int i = 0; i < 3; i++)
        {
            await Assert.That(doc.RootElement[i].ValueKind).IsEqualTo(YamlValueKind.String);
        }
    }

    [Test]
    public async Task JsonSchemaIsStrict()
    {
        using var doc = YamlDocument.Parse("[1, 01, 1.5, .inf, true, True, null, Null]", new YamlDocumentOptions { Schema = YamlSchema.Json });
        YamlValueKind[] k = [YamlValueKind.Integer, YamlValueKind.String, YamlValueKind.Float, YamlValueKind.String,
                             YamlValueKind.Boolean, YamlValueKind.String, YamlValueKind.Null, YamlValueKind.String];
        for (int i = 0; i < k.Length; i++)
        {
            await Assert.That(doc.RootElement[i].ValueKind).IsEqualTo(k[i]);
        }
    }

    [Test]
    public async Task EmptyStreamIsANullDocument()
    {
        using var doc = YamlDocument.Parse("# nothing here\n");
        await Assert.That(doc.RootElement.IsNull).IsTrue();
        await Assert.That(YamlDocument.ParseAll("# nothing\n").Count).IsEqualTo(0);
    }

    [Test]
    public async Task SingleParseRejectsMultipleDocuments()
    {
        Check.Throws<YamlException>(() => YamlDocument.Parse("a\n---\nb\n"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ParseAllReturnsEveryDocument()
    {
        List<YamlDocument> docs = YamlDocument.ParseAll("%YAML 1.2\n---\na: 1\n...\n---\n- x\n- y\n--- plain\n");
        try
        {
            await Assert.That(docs.Count).IsEqualTo(3);
            await Assert.That(docs[0].Version.Major).IsEqualTo(1);
            await Assert.That(docs[0].Version.Minor).IsEqualTo(2);
            await Assert.That(docs[0].RootElement["a"].GetInt32()).IsEqualTo(1);
            await Assert.That(docs[1].RootElement[1].GetString()).IsEqualTo("y");
            await Assert.That(docs[2].RootElement.GetString()).IsEqualTo("plain");
        }
        finally { docs.ForEach(d => d.Dispose()); }
    }

    [Test]
    public async Task SequenceIndexerAndEnumeration()
    {
        using var doc = YamlDocument.Parse("[a, [b, c], {d: e}, f]");
        YamlElement r = doc.RootElement;
        await Assert.That(r.Count).IsEqualTo(4);
        await Assert.That(r[3].GetString()).IsEqualTo("f");
        await Assert.That(r[1][1].GetString()).IsEqualTo("c");
        await Assert.That(r[2]["d"].GetString()).IsEqualTo("e");
        string kinds = string.Join(",", r.EnumerateSequence().Select(e => e.ValueKind));
        await Assert.That(kinds).IsEqualTo("String,Sequence,Mapping,String");
        Check.Throws<ArgumentOutOfRangeException>(() => _ = r[4]);
        Check.Throws<InvalidOperationException>(() => _ = r["x"]);
    }

    [Test]
    public async Task ComplexKeysAreEnumerable()
    {
        using var doc = YamlDocument.Parse("? [a, b]\n: value\n? {k: v}\n: other\n");
        var entries = doc.RootElement.EnumerateMapping().ToList();
        await Assert.That(entries.Count).IsEqualTo(2);
        await Assert.That(entries[0].Key.ValueKind).IsEqualTo(YamlValueKind.Sequence);
        await Assert.That(entries[0].Value.GetString()).IsEqualTo("value");
        YamlElement root = doc.RootElement;
        Check.Throws<InvalidOperationException>(() => root.ToJsonString());
    }

    [Test]
    public async Task CollectionStylesAndTagsAreExposed()
    {
        using var doc = YamlDocument.Parse("a: !!set {x, y}\nb: !local [1]\nc:\n  - 1\n");
        await Assert.That(doc.RootElement["a"].CollectionStyle).IsEqualTo(YamlCollectionStyle.Flow);
        await Assert.That(doc.RootElement["a"].Tag).IsEqualTo("tag:yaml.org,2002:set");
        await Assert.That(doc.RootElement["b"].Tag).IsEqualTo("!local");
        await Assert.That(doc.RootElement["c"].CollectionStyle).IsEqualTo(YamlCollectionStyle.Block);
        await Assert.That(doc.RootElement["c"].Tag).IsNull();
    }

    [Test]
    public async Task Base64Binary()
    {
        using var doc = YamlDocument.Parse("data: !!binary |\n  aGVsbG8g\n  d29ybGQ=\n");
        await Assert.That(Encoding.UTF8.GetString(doc.RootElement["data"].GetBytesFromBase64())).IsEqualTo("hello world");
    }

    [Test]
    public async Task LargeIntegersAndOverflow()
    {
        using var doc = YamlDocument.Parse("[9223372036854775807, -9223372036854775808, 18446744073709551615, 99999999999999999999]");
        await Assert.That(doc.RootElement[0].GetInt64()).IsEqualTo(long.MaxValue);
        await Assert.That(doc.RootElement[1].GetInt64()).IsEqualTo(long.MinValue);
        await Assert.That(doc.RootElement[2].TryGetInt64(out _)).IsFalse();
        await Assert.That(doc.RootElement[2].GetUInt64()).IsEqualTo(ulong.MaxValue);
        await Assert.That(doc.RootElement[3].TryGetUInt64(out _)).IsFalse();
        await Assert.That(doc.RootElement[3].TryGetDouble(out double d) && d > 9.9e19).IsTrue();
    }

    [Test]
    public async Task DisposedDocumentThrows()
    {
        var doc = YamlDocument.Parse("a: 1");
        doc.Dispose();
        Check.Throws<ObjectDisposedException>(() => _ = doc.RootElement);
        await Task.CompletedTask;
    }

    [Test]
    public async Task StreamParsingAndAsync()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("k: [1, 2, 3]\n");
        using (var doc = YamlDocument.Parse(new MemoryStream(bytes)))
        {
            await Assert.That(doc.RootElement["k"].Count).IsEqualTo(3);
        }
        using var doc2 = await YamlDocument.ParseAsync(new MemoryStream(bytes));
        await Assert.That(doc2.RootElement["k"][1].GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task AliasBombJsonExpansionIsBounded()
    {
        var sb = new StringBuilder("a0: &a0 [x, x, x, x, x, x, x, x, x]\n");
        for (int i = 1; i < 9; i++)
        {
            sb.Append($"a{i}: &a{i} [");
            sb.Append(string.Join(", ", Enumerable.Repeat($"*a{i - 1}", 9)));
            sb.Append("]\n");
        }
        using var doc = YamlDocument.Parse(sb.ToString()); // linear in the input
        YamlElement bomb = doc.RootElement["a8"];
        Check.Throws<InvalidOperationException>(() => bomb.WriteTo(new System.Text.Json.Utf8JsonWriter(Stream.Null), maxNodes: 100_000));
        await Task.CompletedTask;
    }
}
