using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Text.Yaml;

internal enum TokenKind : byte
{
    None = 0,
    StreamEnd,
    VersionDirective,
    TagDirective,
    DocumentStart,
    DocumentEnd,
    BlockSequenceStart,
    BlockMappingStart,
    BlockEnd,
    FlowSequenceStart,
    FlowSequenceEnd,
    FlowMappingStart,
    FlowMappingEnd,
    BlockEntry,
    FlowEntry,
    Key,
    Value,
    Alias,
    Anchor,
    Tag,
    Scalar,
}

[Flags]
internal enum TokenFlags : byte
{
    None = 0,
    /// <summary>Scalar contains line breaks that must be folded.</summary>
    Multiline = 1,
    /// <summary>Scalar contains escape sequences / doubled quotes that must be decoded.</summary>
    HasEscapes = 2,
    /// <summary>Block scalar chomping: strip.</summary>
    ChompStrip = 4,
    /// <summary>Block scalar chomping: keep.</summary>
    ChompKeep = 8,
    /// <summary>Tag written as verbatim <c>!&lt;...&gt;</c> (or the bare non-specific <c>!</c>).</summary>
    Verbatim = 16,
}

[StructLayout(LayoutKind.Sequential)]
internal struct Token
{
    public TokenKind Kind;
    public byte Style;
    public TokenFlags Flags;
    public int Start;
    public int End;
    public int A;
    public int B;
}

internal struct SimpleKey
{
    public bool Possible;
    public bool Required;
    /// <summary>Key may span lines (inside a flow mapping).</summary>
    public bool Multiline;
    /// <summary>The key starts right after a tab used as separation/indentation.</summary>
    public bool TabbedStart;
    public long TokenNumber;
    public int Pos;
    public int Line;
    public int Column;
}

internal enum ParserState : byte
{
    ImplicitDocumentStart,
    DocumentStart,
    DocumentContent,
    DocumentEnd,
    BlockNode,
    BlockNodeOrIndentlessSequence,
    FlowNode,
    BlockSequenceFirstEntry,
    BlockSequenceEntry,
    IndentlessSequenceEntry,
    BlockMappingFirstKey,
    BlockMappingKey,
    BlockMappingValue,
    FlowSequenceFirstEntry,
    FlowSequenceEntry,
    FlowSequenceEntryMappingKey,
    FlowSequenceEntryMappingValue,
    FlowSequenceEntryMappingEnd,
    FlowMappingFirstKey,
    FlowMappingKey,
    FlowMappingValue,
    FlowMappingEmptyValue,
    End,
}

/// <summary>
/// A high-performance, forward-only, non-allocating pull parser for YAML 1.2.2 over UTF-8 bytes.
/// It is the YAML counterpart of <c>Utf8JsonReader</c>: a <c>ref struct</c> over a
/// <see cref="ReadOnlySpan{T}"/>, driven by <see cref="Read"/>, exposing the current event through
/// properties. Scalar values are returned zero-copy whenever no folding or escape decoding is needed.
/// </summary>
/// <remarks>
/// Like <c>Utf8JsonReader</c> the reader is a mutable struct: pass it by <c>ref</c>, never copy it while reading.
/// Call <see cref="Dispose"/> (or use <c>using</c>) to return the pooled working buffers.
/// </remarks>
public ref partial struct Utf8YamlReader : IDisposable
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly int _maxDepth;

    // ---- scanner state ----
    private int _pos;
    private int _line;
    private int _lineStart;
    private int _flowLevel;
    private int _indent;
    private bool _simpleKeyAllowed;
    private bool _streamEnded;
    private bool _prevJsonLike;
    private int _tabTokenPos = -1;
    private int _possibleKeys;   // number of simple keys currently marked possible

    private int[]? _indents;
    private int _indentsCount;

    private SimpleKey[]? _simpleKeys;
    private int _simpleKeysCount;

    private Token[]? _tokens;
    private int _tokHead;
    private int _tokCount;
    private long _tokensParsed;

    // ---- parser state ----
    private ParserState _state;
    private ParserState[]? _states;
    private int _statesCount;
    private bool _tokenReady;
    private int _open;
    private bool _versionSeen;

    private int[]? _tagDirectives; // quadruples: handleStart, handleEnd, prefixStart, prefixEnd
    private int _tagDirectiveCount;
    private int _versionMajor;
    private int _versionMinor;

    // ---- current event ----
    private YamlTokenType _tokenType;
    private int _depth;
    private int _eventStart;
    private bool _explicit;
    private YamlScalarStyle _scalarStyle;
    private YamlCollectionStyle _collectionStyle;

    private int _valueStart;
    private int _valueLength;
    private bool _valueInScratch;
    private byte[]? _scratch;

    private int _anchorStart;
    private int _anchorEnd;

    private YamlTagKind _tagKind;
    private int _tagPrefixKind; // 0 none, 1 span in data, 2 "!" , 3 "tag:yaml.org,2002:"
    private int _tagPrefixStart;
    private int _tagPrefixEnd;
    private int _tagSuffixStart;
    private int _tagSuffixEnd;
    private bool _tagSuffixVerbatim;

    private bool _disposed;

    /// <summary>Creates a reader over UTF-8 encoded YAML.</summary>
    /// <param name="yamlData">UTF-8 bytes (a leading BOM is skipped).</param>
    /// <param name="options">Reader options.</param>
    /// <exception cref="YamlException">The data contains ill-formed UTF-8 or a forbidden control character.</exception>
    public Utf8YamlReader(ReadOnlySpan<byte> yamlData, YamlReaderOptions options = default)
    {
        _data = yamlData;
        _maxDepth = options.EffectiveMaxDepth;
        _indent = -1;
        _simpleKeyAllowed = true;
        _state = ParserState.ImplicitDocumentStart;
        _anchorStart = -1;
        _anchorEnd = -1;

        if (!options.SkipInputValidation)
        {
            int bad = YamlEncoding.FindInvalid(yamlData);
            if (bad >= 0)
            {
                ThrowHelper.Throw(yamlData, bad, "The stream contains an invalid or non-printable character");
            }
        }

        if (yamlData.Length >= 3 && yamlData[0] == 0xEF && yamlData[1] == 0xBB && yamlData[2] == 0xBF)
        {
            _pos = 3;
            _lineStart = 3;
        }

        _tokens = ArrayPool<Token>.Shared.Rent(32);
        _indents = ArrayPool<int>.Shared.Rent(32);
        _simpleKeys = ArrayPool<SimpleKey>.Shared.Rent(16);
        _states = ArrayPool<ParserState>.Shared.Rent(32);
        _simpleKeys[0] = default;
        _simpleKeysCount = 1;
    }

    /// <summary>Returns the pooled buffers. The reader must not be used afterwards.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_tokens is { } t) { ArrayPool<Token>.Shared.Return(t); _tokens = null; }
        if (_indents is { } i) { ArrayPool<int>.Shared.Return(i); _indents = null; }
        if (_simpleKeys is { } k) { ArrayPool<SimpleKey>.Shared.Return(k); _simpleKeys = null; }
        if (_states is { } s) { ArrayPool<ParserState>.Shared.Return(s); _states = null; }
        if (_tagDirectives is { } d) { ArrayPool<int>.Shared.Return(d); _tagDirectives = null; }
        if (_scratch is { } c) { ArrayPool<byte>.Shared.Return(c); _scratch = null; }
    }

    // ---------------------------------------------------------------------------------------------
    // Public surface
    // ---------------------------------------------------------------------------------------------

    /// <summary>The kind of the current event.</summary>
    public readonly YamlTokenType TokenType => _tokenType;

    /// <summary>Number of collections currently open around the current event.</summary>
    public readonly int CurrentDepth => _depth;

    /// <summary>
    /// For <see cref="YamlTokenType.StartDocument"/> / <see cref="YamlTokenType.EndDocument"/>: whether the
    /// <c>---</c> / <c>...</c> marker was written. For others: always <c>false</c>.
    /// </summary>
    public readonly bool IsExplicit => _explicit;

    /// <summary>Style of the current scalar.</summary>
    public readonly YamlScalarStyle ScalarStyle => _scalarStyle;

    /// <summary>Style (block/flow) of the current collection start.</summary>
    public readonly YamlCollectionStyle CollectionStyle => _collectionStyle;

    /// <summary>
    /// The decoded value of the current <see cref="YamlTokenType.Scalar"/> (or the anchor name for an
    /// <see cref="YamlTokenType.Alias"/>). Points into the input when no decoding was needed, otherwise into an
    /// internal buffer; valid until the next <see cref="Read"/>.
    /// </summary>
    public readonly ReadOnlySpan<byte> ValueSpan => _valueInScratch
        ? new ReadOnlySpan<byte>(_scratch, 0, _valueLength)
        : _data.Slice(_valueStart, _valueLength);

    /// <summary>Whether the current event carries an anchor (<c>&amp;name</c>).</summary>
    public readonly bool HasAnchor => _anchorStart >= 0;

    /// <summary>The anchor name (without <c>&amp;</c>) of the current node, empty if none.</summary>
    public readonly ReadOnlySpan<byte> Anchor => _anchorStart >= 0 ? _data.Slice(_anchorStart, _anchorEnd - _anchorStart) : default;

    /// <summary>Well-known classification of the current node's tag.</summary>
    public readonly YamlTagKind Tag => _tagKind;

    /// <summary>YAML version from the <c>%YAML</c> directive of the current document (0.0 if none).</summary>
    public readonly (int Major, int Minor) Version => (_versionMajor, _versionMinor);

    /// <summary>Byte offset in the input where the current event begins.</summary>
    public readonly long TokenStartIndex => _eventStart;

    /// <summary>1-based line number of the current event (computed on demand, O(n)).</summary>
    public readonly long LineNumber => PositionOf(_eventStart).Line;

    /// <summary>1-based column of the current event (computed on demand, O(n)).</summary>
    public readonly long ColumnNumber => PositionOf(_eventStart).Column;

    private readonly (long Line, long Column) PositionOf(int offset)
    {
        long line = 1;
        int lineStart = 0;
        ReadOnlySpan<byte> d = _data;
        for (int i = 0; i < offset && i < d.Length; i++)
        {
            byte b = d[i];
            if (b == (byte)'\n' || (b == (byte)'\r' && !(i + 1 < d.Length && d[i + 1] == (byte)'\n')))
            {
                line++;
                lineStart = i + 1;
            }
        }
        return (line, offset - lineStart + 1);
    }

    /// <summary>
    /// Returns the full tag URI of the current node (shorthands expanded using the <c>%TAG</c> directives and
    /// percent-escapes decoded), or <c>null</c> if the node has no tag.
    /// </summary>
    public readonly string? GetTag()
    {
        switch (_tagKind)
        {
            case YamlTagKind.None:
                return null;
            case YamlTagKind.NonSpecific:
                return "!";
        }

        string prefix = _tagPrefixKind switch
        {
            2 => "!",
            3 => "tag:yaml.org,2002:",
            1 => Encoding.UTF8.GetString(_data.Slice(_tagPrefixStart, _tagPrefixEnd - _tagPrefixStart)),
            _ => string.Empty,
        };
        ReadOnlySpan<byte> suffix = _data.Slice(_tagSuffixStart, _tagSuffixEnd - _tagSuffixStart);
        string s = _tagSuffixVerbatim ? Encoding.UTF8.GetString(suffix) : DecodePercent(suffix);
        return prefix.Length == 0 ? s : string.Concat(prefix, s);
    }

    private static string DecodePercent(ReadOnlySpan<byte> s)
    {
        if (s.IndexOf((byte)'%') < 0) return Encoding.UTF8.GetString(s);
        Span<byte> buf = s.Length <= 256 ? stackalloc byte[s.Length] : new byte[s.Length];
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == (byte)'%' && i + 2 < s.Length)
            {
                buf[n++] = (byte)((HexVal(s[i + 1]) << 4) | HexVal(s[i + 2]));
                i += 2;
            }
            else
            {
                buf[n++] = s[i];
            }
        }
        return Encoding.UTF8.GetString(buf.Slice(0, n));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int HexVal(byte c) => c <= (byte)'9' ? c - '0' : (c | 0x20) - 'a' + 10;

    /// <summary>
    /// Skips the current node: for a collection start, reads through its matching end; for a scalar/alias, does nothing.
    /// </summary>
    public void Skip()
    {
        if (_tokenType is YamlTokenType.StartMapping or YamlTokenType.StartSequence)
        {
            int target = _depth;
            while (Read())
            {
                if (_depth == target && _tokenType is YamlTokenType.EndMapping or YamlTokenType.EndSequence) break;
            }
        }
    }

    private readonly void Throw(int offset, string message) => ThrowHelper.Throw(_data, offset, message);
}
