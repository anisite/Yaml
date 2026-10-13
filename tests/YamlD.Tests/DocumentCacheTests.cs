using YamlD.Serialization;

namespace YamlD.Tests;

public class DocumentCacheTests
{
    public sealed class Cible
    {
        public string? Texte { get; set; }
        public List<string>? Liste { get; set; }
        public object? Libre { get; set; }
    }

    private static YamlSerializerOptions AvecCache(YamlDocumentCache cache, bool fusion = false) => new()
    {
        PropertyNamingPolicy = YamlNamingPolicy.CamelCase,
        MergeKeys = fusion,
        DuplicateKeys = YamlDuplicateKeyHandling.Allow,
        DocumentCache = cache,
    };

    [Test]
    public async Task LeCacheEvitelAnalyseMaisChaqueAppelRecoitUnObjetNeuf()
    {
        var cache = new YamlDocumentCache();
        var opts = AvecCache(cache);
        const string yaml = "texte: a\nliste: [x, y]\nlibre: {k: v}";

        Cible premier = YamlSerializer.Deserialize<Cible>(yaml, opts)!;
        premier.Texte = "modifié";
        premier.Liste!.Add("z");
        ((Dictionary<object, object?>)premier.Libre!)["k"] = "changé";

        Cible second = YamlSerializer.Deserialize<Cible>(yaml, opts)!;
        await Assert.That(second).IsNotSameReferenceAs(premier);
        await Assert.That(second.Texte).IsEqualTo("a");
        await Assert.That(second.Liste!.Count).IsEqualTo(2);
        await Assert.That(((Dictionary<object, object?>)second.Libre!)["k"]).IsEqualTo("v");

        await Assert.That(cache.Misses).IsEqualTo(1);
        await Assert.That(cache.Hits).IsEqualTo(1);
        await Assert.That(cache.Count).IsEqualTo(1);
    }

    [Test]
    public async Task LeCacheDistingueLeTexteEtLesOptions()
    {
        var cache = new YamlDocumentCache();
        const string yaml = "libre:\n  base: &b {x: 1}\n  m:\n    <<: *b\n    y: 2";

        var sansFusion = (Dictionary<object, object?>)((Dictionary<object, object?>)YamlSerializer.Deserialize<Cible>(yaml, AvecCache(cache, fusion: false))!.Libre!)["m"]!;
        var avecFusion = (Dictionary<object, object?>)((Dictionary<object, object?>)YamlSerializer.Deserialize<Cible>(yaml, AvecCache(cache, fusion: true))!.Libre!)["m"]!;
        await Assert.That(sansFusion.ContainsKey("<<")).IsTrue();
        await Assert.That(avecFusion.ContainsKey("x")).IsTrue();
        await Assert.That(cache.Count).IsEqualTo(2);

        Cible a = YamlSerializer.Deserialize<Cible>("texte: un", AvecCache(cache))!;
        Cible b = YamlSerializer.Deserialize<Cible>("texte: deux", AvecCache(cache))!;
        await Assert.That(a.Texte).IsEqualTo("un");
        await Assert.That(b.Texte).IsEqualTo("deux");
    }

    [Test]
    public async Task LeCacheEstBorneEtEvinceLePlusAncien()
    {
        var cache = new YamlDocumentCache(maxEntries: 3);
        var opts = AvecCache(cache);
        for (int i = 0; i < 10; i++) YamlSerializer.Deserialize<Cible>($"texte: v{i}", opts);
        await Assert.That(cache.Count).IsEqualTo(3);

        long misses = cache.Misses;
        await Assert.That(YamlSerializer.Deserialize<Cible>("texte: v9", opts)!.Texte).IsEqualTo("v9");
        await Assert.That(cache.Misses).IsEqualTo(misses);
        await Assert.That(YamlSerializer.Deserialize<Cible>("texte: v0", opts)!.Texte).IsEqualTo("v0");
        await Assert.That(cache.Misses).IsEqualTo(misses + 1);
    }

    [Test]
    public async Task UnDocumentPlusGrosQueLeBudgetNEstPasMisEnCache()
    {
        var cache = new YamlDocumentCache(maxBytes: 2000);
        var opts = AvecCache(cache);
        string gros = "texte: " + new string('x', 5000);
        for (int i = 0; i < 2; i++) await Assert.That(YamlSerializer.Deserialize<Cible>(gros, opts)!.Texte!.Length).IsEqualTo(5000);
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(cache.Misses).IsEqualTo(2);
    }

    [Test]
    public async Task LeCacheEstSurPourLesLecturesConcurrentes()
    {
        var cache = new YamlDocumentCache(maxEntries: 4);
        var opts = AvecCache(cache);
        string[] sources = Enumerable.Range(0, 8).Select(i => $"texte: t{i}\nliste: [a{i}, b{i}]\nlibre: {{k: v{i}}}").ToArray();
        int erreurs = 0;

        Parallel.For(0, 4000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, n =>
        {
            int i = n % 8;
            Cible c = YamlSerializer.Deserialize<Cible>(sources[i], opts)!;
            if (c.Texte != $"t{i}" || c.Liste![1] != $"b{i}" || (string?)((Dictionary<object, object?>)c.Libre!)["k"] != $"v{i}")
                Interlocked.Increment(ref erreurs);
        });

        await Assert.That(erreurs).IsEqualTo(0);
        await Assert.That(cache.Count).IsLessThanOrEqualTo(4);
    }

    [Test]
    public async Task LeCacheFonctionneAvecDesOctetsEtCopieLeTampon()
    {
        var cache = new YamlDocumentCache();
        var opts = AvecCache(cache);
        byte[] octets = Encoding.UTF8.GetBytes("texte: abc");
        await Assert.That(YamlSerializer.Deserialize<Cible>(octets, opts)!.Texte).IsEqualTo("abc");

        octets[octets.Length - 1] = (byte)'Z'; // le tampon de l'appelant est réutilisé : le cache ne doit pas en souffrir
        await Assert.That(YamlSerializer.Deserialize<Cible>("texte: abc", opts)!.Texte).IsEqualTo("abc");
        await Assert.That(cache.Hits).IsEqualTo(1);
        await Assert.That(YamlSerializer.Deserialize<Cible>(octets, opts)!.Texte).IsEqualTo("abZ");
    }
}
