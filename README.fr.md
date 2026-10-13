# YamlD

> Version française. English version: [README.md](README.md)

Parseur YAML natif pour .NET 10 : **YAML 1.2.2** complet + les **merge keys `<<`** de YAML 1.1.
Conçu sur le modèle de `System.Text.Json` :

| System.Text.Json | YamlD |
|---|---|
| `Utf8JsonReader` (ref struct, pull, `ReadOnlySpan<byte>`) | `Utf8YamlReader` |
| `JsonDocument` / `JsonElement` (base de métadonnées compacte, buffers poolés) | `YamlDocument` / `YamlElement` |
| `JsonReaderOptions` | `YamlReaderOptions`, `YamlDocumentOptions` |
| `JsonException` | `YamlException` (ligne / colonne / offset) |

## Utilisation

```csharp
// Lecteur pull, zéro allocation, scalaires zéro-copie quand aucun décodage n'est nécessaire
var reader = new Utf8YamlReader(utf8Bytes);
try
{
    while (reader.Read())
    {
        switch (reader.TokenType)
        {
            case YamlTokenType.Scalar:
                ReadOnlySpan<byte> value = reader.ValueSpan;   // décodé (pliage, échappements, block scalars)
                if (reader.HasAnchor) { /* reader.Anchor */ }
                string? tag = reader.GetTag();                 // URI complète, %TAG appliqués
                break;
            case YamlTokenType.StartMapping: /* reader.CollectionStyle, reader.Anchor … */ break;
        }
    }
}
finally { reader.Dispose(); }            // rend les buffers au pool

// DOM
using YamlDocument doc = YamlDocument.Parse(File.ReadAllBytes("config.yaml"));
YamlElement root = doc.RootElement;
int port = root["server"]["port"].GetInt32();
foreach (var (key, value) in root["services"].EnumerateMapping()) { /* <<: *defaults déjà fusionné */ }
string json = root.ToJsonString();

// Flux multi-documents, Stream, UTF-16/32
List<YamlDocument> docs = YamlDocument.ParseAll(stream);
```

Options utiles : `MaxDepth`, `SkipInputValidation`, `MergeKeysDisabled`, `DuplicateKeys` (`Error` par défaut,
conforme à la spec), `Schema` (`Core` par défaut, `Json`, `Failsafe`, `Yaml11`).

## Couverture de la spec

* **yaml-test-suite officielle** (tag `data-2022-01-17`, 402 cas, dont tous les exemples de la spec 1.2.2) :
  * flux d'événements identique à `test.event` pour les 307 cas valides ;
  * les 95 cas invalides sont tous rejetés par une `YamlException` ;
  * pour les 279 cas qui ont un `in.json`, la conversion DOM → JSON est identique.
* Encodages UTF-8 / UTF-16 / UTF-32 (BOM ou détection par motif d'octets nuls), validation UTF-8 et `c-printable`.
* Documents multiples, `---`, `...`, directives `%YAML` (toute version 1.x) et `%TAG`, directives réservées ignorées.
* Styles bloc et flow, clés explicites `?`, clés complexes, mappings à paire unique dans les séquences flow,
  clés JSON-like adjacentes (`{"a":1}`), clés multi-lignes dans les mappings flow.
* Scalaires plain / `'…'` / `"…"` (tous les échappements, paires de substituts), `|` et `>` avec indicateurs
  d'indentation et de chomping (`-`, `+`), pliage de lignes.
* Ancres, alias, tags verbatim / raccourcis / non spécifique, propriétés vides.
* Schémas failsafe, JSON et core (`null`, `bool`, `int` dec/`0o`/`0x`, `float`, `.inf`, `.nan`), tags explicites
  (`!!str`, `!!int`, …), option `Yaml11` (`yes/no/on/off`, octal hérité, `_`, sexagésimal).
* **Merge keys `<<`** : valeur = mapping, alias de mapping, ou séquence de mappings. Les clés explicites priment,
  puis les sources dans l'ordre de la séquence (la première gagne), fusion récursive. Seule la clé plain non taguée
  `<<` (ou `!!merge`) est un merge ; `"<<"` entre guillemets reste une clé ordinaire. La résolution est paresseuse
  (pas de copie, donc pas d'explosion mémoire) ; `Count`, `TryGetProperty` et `EnumerateMapping` voient le résultat fusionné.
* Robustesse : fuzz par mutation (≈ 2 millions de parses) sans autre exception que `YamlException` ni blocage ;
  limite de profondeur ; l'expansion JSON des alias est bornée (`maxNodes`).

## Choix et limites connues

* Entrée entièrement en mémoire (pas de lecture partielle façon `Utf8JsonReader` / `ReadOnlySequence`).
* Pas de writer : lecture seulement. Le désérialiseur (`YamlSerializer`) est décrit plus bas.
* Seuls LF, CR et CRLF sont des fins de ligne (YAML 1.2) ; NEL / LS / PS sont du contenu.
* Le lecteur n'impose pas qu'un alias pointe vers une ancre définie ; le DOM le fait, et rejette aussi les alias
  récursifs (ancre encore ouverte).
* Détection des clés dupliquées : égalité du texte décodé et du type résolu (pas de forme canonique numérique :
  `1` et `0x1` sont distincts).
* Une dernière ligne d'un block scalar composée uniquement d'espaces, en fin de flux, compte comme une ligne
  (c'est ce qu'attend la suite officielle).
* `Utf8YamlReader` est une struct mutable : à passer par `ref`, ne pas la copier pendant la lecture.

## Désérialisation vers des objets (`YamlSerializer`)

```csharp
using YamlD.Serialization;

var options = new YamlSerializerOptions
{
    PropertyNamingPolicy = YamlNamingPolicy.CamelCase,   // comme CamelCaseNamingConvention de YamlDotNet
    MergeKeys = false,                                   // `<<` traité comme une clé ordinaire
    DuplicateKeys = YamlDuplicateKeyHandling.Allow,      // la dernière clé gagne
};
MaConfig? cfg = YamlSerializer.Deserialize<MaConfig>(File.ReadAllBytes("config.yml"), options);
```

Comme `System.Text.Json` : les options sont créées une fois et réutilisées (thread-safe, métadonnées en cache, en lecture seule après
le premier usage), les attributs `System.Text.Json` (`[JsonPropertyName]`, `[JsonIgnore]`, `[JsonConverter]`) s'appliquent et la génération
de code est supportée (`options.CreateJsonSerializerOptions()` pour le contexte, puis les surcharges `JsonTypeInfo<T>`).

**Fonctionnement** : le YAML est parsé par `YamlDocument`, écrit en JSON UTF-8 dans un tampon prêté par `ArrayPool` (un `Utf8JsonWriter`
réutilisé par thread), puis lié par `JsonSerializer`. **Tous les scalaires sont écrits comme chaînes JSON** (`01` reste `"01"` pour un membre
`string`) ; des convertisseurs YAML lisent les types valeur : booléens `true/yes/on/y` (insensibles à la casse), entiers `0x1F` `0b101` `010`
(octal) `1_000` `+5`, flottants `.inf` `.5`, dates, énumérations (nom insensible à la casse ou nombre), `Guid`. Un scalaire nul
(`~`, `null`, vide) donne `default`. Un membre `object` reçoit `string`, `List<object>` et `Dictionary<object, object>`
(`UntypedMappingType = StringKeys` pour `Dictionary<string, object>`), comme YamlDotNet ; `Dictionary<object, TValue>` est supporté.

* `TypeInfoModifiers` : crochet équivalent à `DefaultJsonTypeInfoResolver.Modifiers` (renommer ou retirer des membres sans toucher au modèle).
* `HexByteArrays` : `byte[]` lu depuis une chaîne hexadécimale. `TagMappings` : un mapping tagué instancie un type donné (là où la cible est `object`).
* `DocumentCache` : un `YamlDocumentCache` optionnel, borné et thread-safe, de documents parsés, indexé par le texte YAML exact
  et les options de parsing (les succès sont vérifiés octet par octet). Un succès évite le parsing ; chaque appel lie quand même
  son propre objet neuf, donc rien de mutable n'est partagé. Il s'applique aux surcharges `string` et `ReadOnlyMemory<byte>`,
  pas aux flux, et il est désactivé par défaut.
* Les erreurs de liaison sont des `YamlException` avec le chemin du membre et la ligne/colonne YAML.
* Limites : une clé de mapping qui est une collection (`? [a, b]`) est liée sous sa description (`Mapping[1]`, `Sequence[2]`) ; les tags
  sur des scalaires ne sont pas interprétés ; un tag mappé n'est appliqué que sur une cible `object`.

## Performances (mesurées, BenchmarkDotNet, .NET 10, **Windows 11 ARM64**)

**Lecteur d'événements** (temps moyen, plus petit = mieux ; le gain est calculé par rapport à YamlD) :

| Jeu de données | YamlD | VYaml 1.4 | YamlDotNet 16.3 |
|---|--:|--:|--:|
| config (anchors, merge, block scalars) | **1 204 µs** | 2 513 µs (×2,1) | 10 866 µs (×9,0) |
| flow (JSON-like) | **7 546 µs** | 15 250 µs (×2,0) | 56 090 µs (×7,4) |
| k8s (multi-documents) | **845 µs** | 1 780 µs (×2,1) | 7 893 µs (×9,3) |
| prose (gros scalaires) | **657 µs** | 2 120 µs (×3,2) | 4 632 µs (×7,1) |
| records (séquence de mappings) | **6 988 µs** | 14 502 µs (×2,1) | 76 552 µs (×11,0) |

Mémoire allouée par passe sur ces mêmes jeux :

| Jeu de données | YamlD | VYaml | YamlDotNet |
|---|--:|--:|--:|
| config | 3 B | 385 656 B | 9 873 940 B |
| flow | 162 B | 17 128 B | 61 639 512 B |
| k8s | 2 B | 1 136 B | 7 965 231 B |
| prose | 3 B | 1 136 B | 2 742 209 B |
| records | 11 B | 1 136 B | 66 723 040 B |

**DOM** : VYaml n'a pas de DOM, la comparaison se fait donc avec `YamlStream.Load` de YamlDotNet
(`YamlDocument.Parse` avec détection des clés dupliquées activée) :

| Jeu de données | YamlD | YamlDotNet | Mémoire : nous → YamlDotNet |
|---|--:|--:|--:|
| config | **1,45 ms** | 13,19 ms (×9,1) | 3,6 Ko → 12,4 Mo |
| k8s (`ParseAll`, 400 documents) | **2,34 ms** | 11,12 ms (×4,8) | 360 Ko → 11,4 Mo |
| records | **10,5 ms** | 103,2 ms (×9,8) | 64 Ko → 94,2 Mo |

**Désérialisation typée** (`YamlSerializer.Deserialize<List<Record>>` ; chaque enregistrement contient des objets imbriqués,
des listes, un booléen, un double et un null ; YamlDotNet est configuré avec `CamelCaseNamingConvention`). « Avec cache » est
l'option `DocumentCache` (ci-dessus) quand le même texte est relu :

| Entrée | YamlDotNet | YamlD | YamlD avec cache |
|---|--:|--:|--:|
| 1 enregistrement (13 lignes) | 23,1 µs | **3,5 µs** (×6,6) | **1,9 µs** (×12,1) |
| 100 enregistrements (1 300 lignes) | 2,26 ms | **328 µs** (×6,9) | **172 µs** (×13,2) |
| 3 846 enregistrements (50 000 lignes) | 96,0 ms | **13,4 ms** (×7,2) | **7,4 ms** (×12,9) |

Mémoire allouée par appel : 33,8 Ko / 2,5 Mo / 98,3 Mo (YamlDotNet), 1,5 Ko / 91 Ko / 3,9 Mo (YamlD), 1,4 Ko / 91 Ko / 3,6 Mo
(YamlD avec cache). Un succès du cache hache quand même l'entrée, la compare octet par octet et lie un objet neuf : seul le
parsing est évité, d'où un gain d'environ 2× ici. Un échec du cache coûte autant que sans cache, plus l'insertion. YamlDotNet
reçoit une `string` déjà décodée et YamlD les octets UTF-8. Le désérialiseur typé de VYaml n'est pas comparé (il exige des
types générés par source generator).

La vérification des doublons coûtait environ 40 % du DOM sur `records` ; elle a été optimisée après cette mesure
(comparaison des longueurs avant les octets), donc le DOM est aujourd'hui plus rapide que ces chiffres
(`--quick` : 179 Mo/s sur `records`, contre 209 Mo/s pour le lecteur seul). Ces lignes DOM ne sont pas remesurées
avec BenchmarkDotNet.

Ces chiffres ne valent que pour cette machine et ces jeux de données synthétiques ; relancez-les sur votre matériel
(x64 notamment, non mesuré ici). Ils ne prouvent pas que ce soit « le plus rapide au monde » : seulement que c'est
plus rapide que VYaml et YamlDotNet dans ces scénarios.

## Commandes

```powershell
dotnet test --project tests/YamlD.Tests -c Release      # TUnit : suite officielle + tests unitaires + fuzz
dotnet run -c Release --project tools/SuiteRunner -- tests/YamlD.Tests/suite          # rapport de conformité
dotnet run -c Release --project tools/SuiteRunner -- fuzz tests/YamlD.Tests/suite 400 1 # fuzz long
cd bench/YamlD.Benchmarks
dotnet run -c Release -- --check                                  # vérifie que les 3 parseurs voient le même contenu
dotnet run -c Release -- --quick                                  # comparaison rapide (Mo/s)
dotnet run -c Release -- --filter *ParseEvents*                   # BenchmarkDotNet complet
```

Les données de test proviennent de [yaml-test-suite](https://github.com/yaml/yaml-test-suite) (licence MIT, voir
`tests/YamlD.Tests/suite/LICENSE`).
