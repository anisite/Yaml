// The reflection-based path is the default one; AOT/trimmed apps use the JsonTypeInfo<T> overloads (source generation).
#pragma warning disable IL2026, IL3050, IL2071
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace System.Text.Yaml.Serialization;

/// <summary>Type that <see cref="YamlSerializer"/> creates for an untyped (<c>object</c>) YAML mapping.</summary>
public enum YamlUntypedMappingType : byte
{
    /// <summary><c>Dictionary&lt;object, object?&gt;</c> (the keys are the key strings). Matches what YamlDotNet builds for <c>object</c>/<c>dynamic</c>.</summary>
    ObjectKeys = 0,

    /// <summary><c>Dictionary&lt;string, object?&gt;</c>.</summary>
    StringKeys,
}

/// <summary>
/// Options for <see cref="YamlSerializer"/>. Same contract as <see cref="JsonSerializerOptions"/>: configure once, then reuse the
/// instance (it is thread-safe and caches type metadata); the options become read-only after the first use.
/// </summary>
/// <remarks>
/// <para>
/// Deserialization works in two steps, both streaming and allocation-light: the YAML document is parsed by
/// <see cref="YamlDocument"/> and rendered as UTF-8 JSON into a pooled buffer in which <b>every scalar is a string</b>
/// (so <c>01</c> stays <c>"01"</c> for a <see cref="string"/> member), then <see cref="JsonSerializer"/> binds it
/// to the target type. The target type therefore decides how a scalar is read: YAML scalars are converted by
/// the YAML-aware converters registered here (<c>yes/no</c>, <c>0x1F</c>, <c>0o17</c>, <c>1_000</c>, <c>.inf</c>, dates…), and the usual
/// <c>System.Text.Json</c> attributes (<see cref="JsonPropertyNameAttribute"/>, <see cref="JsonIgnoreAttribute"/>,
/// <see cref="JsonConverterAttribute"/>…) and source generation (<see cref="JsonTypeInfo{T}"/>) apply.
/// </para>
/// </remarks>
public sealed class YamlSerializerOptions
{
    private bool _frozen;
    private JsonNamingPolicy? _propertyNamingPolicy;
    private bool _includeFields = true;
    private bool _mergeKeys = true;
    private YamlDuplicateKeyHandling _duplicateKeys = YamlDuplicateKeyHandling.Error;
    private YamlUntypedMappingType _untypedMappingType;
    private bool _hexByteArrays;
    private YamlDocumentCache? _documentCache;
    private int _maxDepth;
    private long _maxNodes = 10_000_000;
    private JsonSerializerOptions? _jsonOptions;
    private HashSet<string>? _tagSet;

    private readonly List<JsonConverter> _converters = new();
    private readonly List<Action<JsonTypeInfo>> _typeInfoModifiers = new();
    private readonly Dictionary<string, Type> _tagMappings = new(StringComparer.Ordinal);

    /// <summary>Default options: YAML 1.2.2 behavior (merge keys on, duplicate keys rejected), member names used as is, public fields included.</summary>
    public static YamlSerializerOptions Default { get; } = new();

    /// <summary>Naming policy applied to member names (default: none, the member name is used as is).</summary>
    public JsonNamingPolicy? PropertyNamingPolicy
    {
        get => _propertyNamingPolicy;
        set { Check(); _propertyNamingPolicy = value; }
    }

    /// <summary>Whether public fields are bound like properties (default <c>true</c>).</summary>
    public bool IncludeFields
    {
        get => _includeFields;
        set { Check(); _includeFields = value; }
    }

    /// <summary>Whether the YAML 1.1 merge key (<c>&lt;&lt;</c>) is applied (default <c>true</c>). When <c>false</c> it is an ordinary key.</summary>
    public bool MergeKeys
    {
        get => _mergeKeys;
        set { Check(); _mergeKeys = value; }
    }

    /// <summary>
    /// How duplicate mapping keys are handled (default <see cref="YamlDuplicateKeyHandling.Error"/>, as the specification
    /// requires). With <see cref="YamlDuplicateKeyHandling.Allow"/> the last occurrence wins.
    /// </summary>
    public YamlDuplicateKeyHandling DuplicateKeys
    {
        get => _duplicateKeys;
        set { Check(); _duplicateKeys = value; }
    }

    /// <summary>The dictionary type built for an <c>object</c>-typed mapping (default <see cref="YamlUntypedMappingType.ObjectKeys"/>).</summary>
    public YamlUntypedMappingType UntypedMappingType
    {
        get => _untypedMappingType;
        set { Check(); _untypedMappingType = value; }
    }

    /// <summary>When <c>true</c>, a <c>byte[]</c> is read from a hexadecimal string (default: base64, as in System.Text.Json).</summary>
    public bool HexByteArrays
    {
        get => _hexByteArrays;
        set { Check(); _hexByteArrays = value; }
    }

    /// <summary>
    /// Optional cache of parsed documents (default <c>null</c>: none). A hit skips the parsing, which dominates the cost, and the
    /// object is still bound afresh on every call, so callers never share mutable state. Applies to the <see cref="string"/> and
    /// <see cref="ReadOnlyMemory{T}"/> overloads, not to streams.
    /// </summary>
    public YamlDocumentCache? DocumentCache
    {
        get => _documentCache;
        set { Check(); _documentCache = value; }
    }

    /// <summary>Maximum nesting depth (0 = default, 256).</summary>
    public int MaxDepth
    {
        get => _maxDepth;
        set { Check(); _maxDepth = value; }
    }

    /// <summary>Upper bound on the number of nodes produced once aliases are expanded (protection against alias bombs).</summary>
    public long MaxNodes
    {
        get => _maxNodes;
        set { Check(); _maxNodes = value; }
    }

    /// <summary>Additional converters (the YAML-aware ones are always registered first).</summary>
    public IList<JsonConverter> Converters => _frozen ? throw ReadOnly() : _converters;

    /// <summary>
    /// Callbacks that adjust the reflection-based metadata of every type before first use (rename or remove members, set
    /// <see cref="JsonPropertyInfo.ShouldSerialize"/>…): the equivalent of <see cref="DefaultJsonTypeInfoResolver.Modifiers"/>.
    /// Lets a model keep its own attributes (for example another library's) without adding <c>System.Text.Json</c> ones.
    /// </summary>
    public IList<Action<JsonTypeInfo>> TypeInfoModifiers => _frozen ? throw ReadOnly() : _typeInfoModifiers;

    /// <summary>
    /// Maps a full tag URI (for example <c>tag:yaml.org,2002:Function</c> for <c>!!Function</c>) to the .NET type
    /// instantiated for a tagged <i>mapping</i> found where the target is <see cref="object"/>.
    /// </summary>
    public IDictionary<string, Type> TagMappings => _frozen ? throw ReadOnly() : _tagMappings;

    internal YamlDocumentOptions DocumentOptions => new()
    {
        MaxDepth = _maxDepth,
        MergeKeysDisabled = !_mergeKeys,
        DuplicateKeys = _duplicateKeys,
        Schema = YamlSchema.Core,
    };

    internal HashSet<string>? MappedTags => _tagSet;

    /// <summary>The reflection-based <see cref="JsonSerializerOptions"/> used internally (built once, then shared).</summary>
    internal JsonSerializerOptions JsonOptions
    {
        get
        {
            JsonSerializerOptions? o = _jsonOptions;
            if (o is not null) return o;
            lock (_converters)
            {
                return _jsonOptions ??= Create(reflection: true);
            }
        }
    }

    /// <summary>
    /// Creates a new <see cref="JsonSerializerOptions"/> carrying this configuration (YAML converters, naming policy…), to hand to a
    /// source-generated <see cref="JsonSerializerContext"/>: <c>new MyContext(yamlOptions.CreateJsonSerializerOptions())</c>, then pass
    /// <c>MyContext.Default.MyType</c> to <see cref="YamlSerializer.Deserialize{T}(string, JsonTypeInfo{T}, YamlSerializerOptions)"/>.
    /// </summary>
    public JsonSerializerOptions CreateJsonSerializerOptions()
    {
        lock (_converters)
        {
            return Create(reflection: false);
        }
    }

    private JsonSerializerOptions Create(bool reflection)
    {
        _tagSet = _tagMappings.Count == 0 ? null : new HashSet<string>(_tagMappings.Keys, StringComparer.Ordinal);

        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = _propertyNamingPolicy,
            IncludeFields = _includeFields,
            MaxDepth = _maxDepth > 0 ? _maxDepth : 256,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        };
        if (reflection)
        {
            var resolver = new DefaultJsonTypeInfoResolver();
            foreach (Action<JsonTypeInfo> m in _typeInfoModifiers) resolver.Modifiers.Add(m);
            o.TypeInfoResolver = resolver;
        }

        // Order matters: the first converter that accepts a type wins.
        o.Converters.Add(new YamlObjectConverter(_untypedMappingType == YamlUntypedMappingType.StringKeys, _tagMappings.Count == 0 ? null : new Dictionary<string, Type>(_tagMappings)));
        foreach (JsonConverter c in _converters) o.Converters.Add(c);
        if (_hexByteArrays) o.Converters.Add(new YamlHexByteArrayConverter());
        YamlConverters.AddTo(o.Converters);

        _frozen = true;
        return o;
    }

    private void Check()
    {
        if (_frozen) throw ReadOnly();
    }

    private static InvalidOperationException ReadOnly() =>
        new("This YamlSerializerOptions instance is read-only: it has already been used to (de)serialize. Create a new instance to change settings.");
}
