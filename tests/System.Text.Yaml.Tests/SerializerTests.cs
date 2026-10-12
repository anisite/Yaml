using System.Text.Json.Serialization;
using System.Text.Yaml.Serialization;

namespace System.Text.Yaml.Tests;

public class SerializerTests
{
    private static readonly YamlSerializerOptions Frw = new()
    {
        PropertyNamingPolicy = YamlNamingPolicy.CamelCase,
        MergeKeys = false,
        DuplicateKeys = YamlDuplicateKeyHandling.Allow,
    };

    public enum Niveau { Bas, Haut }

    public sealed class Cible
    {
        public string? Texte { get; set; }
        public int Entier { get; set; }
        public int? EntierNullable { get; set; }
        public double Reel { get; set; }
        public bool Actif { get; set; }
        public DateTime Date { get; set; }
        public Niveau Niveau { get; set; }
        public Guid Id { get; set; }
        public List<string>? Liste { get; set; }
        public Dictionary<string, object>? Dico { get; set; }
        public object? Libre { get; set; }
        public IEnumerable<dynamic>? Etapes;

        [JsonPropertyName("section")]
        public string? Nom { get; set; }

        public string? URL { get; set; }
    }

    private static Cible Lire(string yaml, YamlSerializerOptions? options = null) =>
        YamlSerializer.Deserialize<Cible>(yaml, options ?? Frw)!;

    [Test]
    public async Task UnScalaireTexteGardeSonTexteExact()
    {
        Cible c = Lire("texte: 01");
        await Assert.That(c.Texte).IsEqualTo("01");
        c = Lire("texte: 1.50");
        await Assert.That(c.Texte).IsEqualTo("1.50");
        c = Lire("texte: yes");
        await Assert.That(c.Texte).IsEqualTo("yes");
        c = Lire("texte: \"null\"");
        await Assert.That(c.Texte).IsEqualTo("null");
    }

    [Test]
    public async Task NullVideEtTildeDonnentNullMaisGuillemetsVideDonneVide()
    {
        await Assert.That(Lire("texte:").Texte).IsNull();
        await Assert.That(Lire("texte: ~").Texte).IsNull();
        await Assert.That(Lire("texte: null").Texte).IsNull();
        await Assert.That(Lire("texte: ''").Texte).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task LesNombresAcceptentLesFormesYaml()
    {
        await Assert.That(Lire("entier: 0x1F").Entier).IsEqualTo(31);
        await Assert.That(Lire("entier: 0b101").Entier).IsEqualTo(5);
        await Assert.That(Lire("entier: 010").Entier).IsEqualTo(8);
        await Assert.That(Lire("entier: 1_000").Entier).IsEqualTo(1000);
        await Assert.That(Lire("entier: +5").Entier).IsEqualTo(5);
        await Assert.That(Lire("entier:").Entier).IsEqualTo(0);
        await Assert.That(Lire("entierNullable:").EntierNullable).IsNull();
        await Assert.That(Lire("entierNullable: 7").EntierNullable).IsEqualTo(7);
        await Assert.That(Lire("reel: .5").Reel).IsEqualTo(0.5);
        await Assert.That(double.IsPositiveInfinity(Lire("reel: .inf").Reel)).IsTrue();
    }

    [Test]
    public async Task UnEntierInvalideOuHorsLimiteEchoueAvecLeChemin()
    {
        YamlException ex = Assert.Throws<YamlException>(() => Lire("entier: 2147483648"));
        await Assert.That(ex.Message).Contains("$.entier");
        Assert.Throws<YamlException>(() => Lire("entier: abc"));
        Assert.Throws<YamlException>(() => Lire("entier: 1.5"));
    }

    [Test]
    public async Task LesBooleensAcceptentLaCasseEtLesFormesYaml11()
    {
        foreach (string v in new[] { "true", "True", "TRUE", "tRue", "yes", "Yes", "on", "y" })
            await Assert.That(Lire($"actif: {v}").Actif).IsTrue();
        foreach (string v in new[] { "false", "no", "NO", "off", "n" })
            await Assert.That(Lire($"actif: {v}").Actif).IsFalse();
        Assert.Throws<YamlException>(() => Lire("actif: 1"));
        Assert.Throws<YamlException>(() => Lire("actif: maybe"));
    }

    [Test]
    public async Task EnumsDatesEtGuid()
    {
        await Assert.That(Lire("niveau: haut").Niveau).IsEqualTo(Niveau.Haut);
        await Assert.That(Lire("niveau: 1").Niveau).IsEqualTo(Niveau.Haut);
        Assert.Throws<YamlException>(() => Lire("niveau: Inconnu"));
        await Assert.That(Lire("date: 2023-04-17").Date).IsEqualTo(new DateTime(2023, 4, 17));
        await Assert.That(Lire("date: 2023-04-17 10:00:00").Date).IsEqualTo(new DateTime(2023, 4, 17, 10, 0, 0));
        Guid g = Guid.NewGuid();
        await Assert.That(Lire($"id: {g}").Id).IsEqualTo(g);
    }

    [Test]
    public async Task UnObjetNonTypeDonneDictionnaireObjetEtListeDeChaines()
    {
        Cible c = Lire("""
            libre:
              a: 1
              b: [x, {c: d}]
              e: ~
            """);
        var d = (Dictionary<object, object?>)c.Libre!;
        await Assert.That(d["a"]).IsEqualTo("1");
        var b = (List<object?>)d["b"]!;
        await Assert.That(b[0]).IsEqualTo("x");
        await Assert.That(((Dictionary<object, object?>)b[1]!)["c"]).IsEqualTo("d");
        await Assert.That(d["e"]).IsNull();
    }

    [Test]
    public async Task LeTypeDuDictionnaireNonTypeEstConfigurable()
    {
        var opts = new YamlSerializerOptions { PropertyNamingPolicy = YamlNamingPolicy.CamelCase, UntypedMappingType = YamlUntypedMappingType.StringKeys };
        Cible c = Lire("libre: {a: 1}", opts);
        await Assert.That(c.Libre).IsTypeOf<Dictionary<string, object?>>();
        c = Lire("dico: {a: {b: [1]}}", opts);
        await Assert.That(c.Dico!["a"]).IsTypeOf<Dictionary<string, object?>>();
    }

    [Test]
    public async Task UnChampPublicEtUneInterfaceDynamicFonctionnent()
    {
        Cible c = Lire("""
            etapes:
            - tache: a
              n: 1
            - tache: b
            """);
        var etapes = c.Etapes!.ToList();
        await Assert.That(etapes.Count).IsEqualTo(2);
        await Assert.That(((Dictionary<object, object?>)etapes[0])["tache"]).IsEqualTo("a");
    }

    [Test]
    public async Task LaPolitiqueDeNommageReproduitYamlDotNetEtJsonPropertyNameEstExact()
    {
        Cible c = Lire("uRL: x\nsection: S");
        await Assert.That(c.URL).IsEqualTo("x");
        await Assert.That(c.Nom).IsEqualTo("S");
        await Assert.That(YamlNamingPolicy.CamelCase.ConvertName("URL")).IsEqualTo("uRL");
        await Assert.That(YamlNamingPolicy.CamelCase.ConvertName("snake_case_name")).IsEqualTo("snakeCaseName");
        await Assert.That(YamlNamingPolicy.CamelCase.ConvertName("IdSystemeAutorise")).IsEqualTo("idSystemeAutorise");
    }

    [Test]
    public async Task LesCleInconnuesSontIgnoreesEtLeDocumentVideDonneNull()
    {
        await Assert.That(Lire("inconnue: 1\ntexte: a").Texte).IsEqualTo("a");
        await Assert.That(YamlSerializer.Deserialize<Cible>("", Frw)).IsNull();
        await Assert.That(YamlSerializer.Deserialize<Cible>("# rien\n", Frw)).IsNull();
        await Assert.That(YamlSerializer.Deserialize<Cible>("~", Frw)).IsNull();
    }

    [Test]
    public async Task LesClesDupliqueesSuiventLOption()
    {
        await Assert.That(Lire("texte: a\ntexte: b").Texte).IsEqualTo("b");
        Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<Cible>("texte: a\ntexte: b", new YamlSerializerOptions()));
    }

    [Test]
    public async Task LesMergeKeysSuiventLOption()
    {
        const string yaml = "libre:\n  base: &b {x: 1}\n  m:\n    <<: *b\n    y: 2";
        var off = (Dictionary<object, object?>)((Dictionary<object, object?>)Lire(yaml).Libre!)["m"]!;
        await Assert.That(off.ContainsKey("<<")).IsTrue();
        var on = (Dictionary<object, object?>)((Dictionary<object, object?>)Lire(yaml, new YamlSerializerOptions { PropertyNamingPolicy = YamlNamingPolicy.CamelCase }).Libre!)["m"]!;
        await Assert.That(on["x"]).IsEqualTo("1");
        await Assert.That(on.ContainsKey("<<")).IsFalse();
    }

    [Test]
    public async Task LesAliasSontDeveloppes()
    {
        var d = YamlSerializer.Deserialize<Dictionary<string, List<string>>>("a: &l [x, y]\nb: *l", Frw)!;
        await Assert.That(d["b"].Count).IsEqualTo(2);
    }

    [Test]
    public async Task UnDictionnaireAvecCleObjetEstSupporte()
    {
        var d = YamlSerializer.Deserialize<Dictionary<object, object>>("a: 1\nb: [1, 2]\nc: {d: e}", Frw)!;
        await Assert.That(d["a"]).IsEqualTo("1");
        await Assert.That(d["c"]).IsTypeOf<Dictionary<object, object?>>();
    }

    [Test]
    public async Task LesOctetsHexadecimauxSontOptionnels()
    {
        var hex = new YamlSerializerOptions { HexByteArrays = true };
        var r = YamlSerializer.Deserialize<Dictionary<string, byte[]>>("b: AFF10", hex)!;
        await Assert.That(Convert.ToHexString(r["b"])).IsEqualTo("0AFF10");
    }

    [Test]
    public async Task UnTexteAvecAccentsEtEchappementsPasseIntact()
    {
        var d = YamlSerializer.Deserialize<Dictionary<string, string>>("a: 'é à ü ç «x»'\nb: \"tab\\t \\\"q\\\" \\\\\"", Frw)!;
        await Assert.That(d["a"]).IsEqualTo("é à ü ç «x»");
        await Assert.That(d["b"]).IsEqualTo("tab\t \"q\" \\");
    }

    [Test]
    public async Task LesOptionsSontEnLectureSeuleApresUsage()
    {
        var o = new YamlSerializerOptions();
        _ = YamlSerializer.Deserialize<Dictionary<string, string>>("a: b", o);
        Assert.Throws<InvalidOperationException>(() => o.MergeKeys = false);
    }

    [Test]
    public async Task UneBombeDAliasEstBornee()
    {
        var sb = new StringBuilder("a0: &a0 [x, x, x, x, x, x, x, x, x, x]\n");
        for (int i = 1; i < 12; i++)
        {
            string prev = "*a" + (i - 1);
            sb.Append($"a{i}: &a{i} [{string.Join(", ", Enumerable.Repeat(prev, 10))}]\n");
        }
        var o = new YamlSerializerOptions { MaxNodes = 100_000 };
        Assert.Throws<InvalidOperationException>(() => YamlSerializer.Deserialize<Dictionary<string, object>>(sb.ToString(), o));
        await Task.CompletedTask;
    }
}
