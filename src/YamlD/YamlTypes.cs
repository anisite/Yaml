namespace YamlD;

/// <summary>Kind of event produced by <see cref="Utf8YamlReader"/> (the YAML analogue of <c>JsonTokenType</c>).</summary>
public enum YamlTokenType : byte
{
    /// <summary>No event has been read yet.</summary>
    None = 0,
    /// <summary>Start of a document. See <see cref="Utf8YamlReader.IsExplicit"/>.</summary>
    StartDocument,
    /// <summary>End of a document. See <see cref="Utf8YamlReader.IsExplicit"/>.</summary>
    EndDocument,
    /// <summary>Start of a mapping (block or flow). Anchor/tag describe the mapping node.</summary>
    StartMapping,
    /// <summary>End of a mapping.</summary>
    EndMapping,
    /// <summary>Start of a sequence (block or flow). Anchor/tag describe the sequence node.</summary>
    StartSequence,
    /// <summary>End of a sequence.</summary>
    EndSequence,
    /// <summary>A scalar node; the decoded content is in <see cref="Utf8YamlReader.ValueSpan"/>.</summary>
    Scalar,
    /// <summary>An alias node (<c>*name</c>); the name is in <see cref="Utf8YamlReader.ValueSpan"/>.</summary>
    Alias,
}

/// <summary>Presentation style of a scalar node.</summary>
public enum YamlScalarStyle : byte
{
    Plain = 0,
    SingleQuoted = 1,
    DoubleQuoted = 2,
    Literal = 3,
    Folded = 4,
}

/// <summary>Presentation style of a collection node.</summary>
public enum YamlCollectionStyle : byte
{
    Block = 0,
    Flow = 1,
}

/// <summary>Well-known tag carried by a node, resolved against the active <c>%TAG</c> directives.</summary>
public enum YamlTagKind : byte
{
    /// <summary>No tag was written.</summary>
    None = 0,
    /// <summary>The non-specific tag <c>!</c>.</summary>
    NonSpecific,
    /// <summary><c>tag:yaml.org,2002:str</c></summary>
    Str,
    /// <summary><c>tag:yaml.org,2002:int</c></summary>
    Int,
    /// <summary><c>tag:yaml.org,2002:float</c></summary>
    Float,
    /// <summary><c>tag:yaml.org,2002:bool</c></summary>
    Bool,
    /// <summary><c>tag:yaml.org,2002:null</c></summary>
    Null,
    /// <summary><c>tag:yaml.org,2002:seq</c></summary>
    Seq,
    /// <summary><c>tag:yaml.org,2002:map</c></summary>
    Map,
    /// <summary><c>tag:yaml.org,2002:merge</c> (YAML 1.1 merge key type).</summary>
    Merge,
    /// <summary>Any other tag (use <see cref="Utf8YamlReader.GetTag"/>).</summary>
    Other,
}

/// <summary>Result of resolving a scalar against a schema.</summary>
public enum YamlScalarKind : byte
{
    String = 0,
    Null,
    Boolean,
    Integer,
    Float,
}

/// <summary>Schema used to resolve untagged plain scalars (YAML 1.2.2 chapter 10).</summary>
public enum YamlSchema : byte
{
    /// <summary>Core schema (default): <c>null</c>, <c>bool</c>, <c>int</c> (dec/0o/0x), <c>float</c> (<c>.inf</c>, <c>.nan</c>).</summary>
    Core = 0,
    /// <summary>JSON schema: strict JSON-compatible literals only.</summary>
    Json,
    /// <summary>Failsafe schema: every scalar is a string.</summary>
    Failsafe,
    /// <summary>Core schema plus the YAML 1.1 literals (<c>yes/no/on/off</c>, <c>y/n</c>, legacy octal, <c>_</c> separators, sexagesimal).</summary>
    Yaml11,
}

/// <summary>How duplicate mapping keys are handled when building a <see cref="YamlDocument"/>.</summary>
public enum YamlDuplicateKeyHandling : byte
{
    /// <summary>Duplicate keys are a parse error (YAML 1.2.2 §3.2.1.3).</summary>
    Error = 0,
    /// <summary>Duplicate keys are kept; lookups return the last occurrence.</summary>
    Allow,
}

/// <summary>Options for <see cref="Utf8YamlReader"/>.</summary>
public struct YamlReaderOptions
{
    /// <summary>Maximum nesting depth of collections. 0 means the default (256).</summary>
    public int MaxDepth { get; set; }

    /// <summary>
    /// When <c>true</c> the input is not checked for well-formed UTF-8 and YAML <c>c-printable</c> characters
    /// up front. Faster, but invalid input may then produce undefined (yet memory-safe) results.
    /// </summary>
    public bool SkipInputValidation { get; set; }

    internal readonly int EffectiveMaxDepth => MaxDepth <= 0 ? 256 : MaxDepth;
}

/// <summary>Options for <see cref="YamlDocument"/> parsing.</summary>
public struct YamlDocumentOptions
{
    /// <summary>Maximum nesting depth of collections. 0 means the default (256).</summary>
    public int MaxDepth { get; set; }

    /// <summary>Skip the up-front UTF-8 / printable-character validation.</summary>
    public bool SkipInputValidation { get; set; }

    /// <summary>Disable the YAML 1.1 merge key (<c>&lt;&lt;</c>) handling, which is on by default.</summary>
    public bool MergeKeysDisabled { get; set; }

    /// <summary>Behavior for duplicate mapping keys. Default: <see cref="YamlDuplicateKeyHandling.Error"/>.</summary>
    public YamlDuplicateKeyHandling DuplicateKeys { get; set; }

    /// <summary>Schema used by <see cref="YamlElement.ValueKind"/> and the numeric accessors.</summary>
    public YamlSchema Schema { get; set; }

    internal readonly YamlReaderOptions ToReaderOptions() => new()
    {
        MaxDepth = MaxDepth,
        SkipInputValidation = SkipInputValidation,
    };
}
