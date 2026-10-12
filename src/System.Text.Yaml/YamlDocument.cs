using System.Buffers;
using System.Runtime.CompilerServices;

namespace System.Text.Yaml;

/// <summary>Resolved kind of a <see cref="YamlElement"/> (alias nodes are transparent).</summary>
public enum YamlValueKind : byte
{
    Undefined = 0,
    Mapping,
    Sequence,
    String,
    Integer,
    Float,
    Boolean,
    Null,
}

internal struct DbRow
{
    /// <summary>Scalar: offset of the value (input or pool); alias: index of the target row.</summary>
    public int Location;
    /// <summary>Scalar: byte length; mapping: number of pairs; sequence: number of items.</summary>
    public int Length;
    /// <summary>Number of rows of this node's subtree, itself included (0 while a collection is still open).</summary>
    public int Rows;
    public uint Info;
}

internal static class RowInfo
{
    public const uint TypeMask = 7;
    public const uint Scalar = 0;
    public const uint Mapping = 1;
    public const uint Sequence = 2;
    public const uint Alias = 3;

    public const uint InPool = 1u << 3;
    public const int StyleShift = 4;       // 3 bits
    public const uint Flow = 1u << 7;
    public const int TagShift = 8;         // 4 bits
    public const uint HasProps = 1u << 12;
    public const uint HasMerge = 1u << 13;
    public const uint IsMergeKey = 1u << 14;
    public const uint Complex = 1u << 15;  // collection has at least one non-scalar child

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Type(uint info) => info & TypeMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static YamlScalarStyle Style(uint info) => (YamlScalarStyle)((info >> StyleShift) & 7);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static YamlTagKind Tag(uint info) => (YamlTagKind)((info >> TagShift) & 15);
}

internal struct NodeProps
{
    public int Row;
    public int AnchorStart;
    public int AnchorLength;   // 0 => no anchor
    public byte PrefixKind;
    public int PrefixStart;
    public int PrefixLength;
    public int SuffixStart;
    public int SuffixLength;
    public bool SuffixVerbatim;
}

/// <summary>
/// An immutable, parsed YAML document: a compact, pooled, index-based representation in the spirit of
/// <c>JsonDocument</c>. Aliases are resolved to their anchored node without copying, YAML 1.1 merge keys
/// (<c>&lt;&lt;</c>) are honoured by lookups and enumeration, and scalars are decoded once.
/// </summary>
/// <remarks>Dispose the document to return its pooled buffers.</remarks>
public sealed partial class YamlDocument : IDisposable
{
    private ReadOnlyMemory<byte> _data;
    private DataOwner? _owner;
    private DbRow[]? _rows;
    private int _rowCount;
    private byte[]? _pool;
    private int _poolLength;
    private NodeProps[]? _props;
    private int _propCount;
    private readonly YamlSchema _schema;

    internal YamlSchema Schema => _schema;

    /// <summary>The <c>%YAML</c> version of the document (0.0 when no directive was given).</summary>
    public (int Major, int Minor) Version { get; private set; }

    /// <summary>Whether the document started with an explicit <c>---</c>.</summary>
    public bool HasExplicitStart { get; private set; }

    private YamlDocument(ReadOnlyMemory<byte> data, DataOwner? owner, YamlSchema schema)
    {
        _data = data;
        _owner = owner;
        _schema = schema;
        owner?.AddRef();
    }

    /// <summary>The root node.</summary>
    public YamlElement RootElement
    {
        get
        {
            if (_rows is null) ThrowHelper.ThrowObjectDisposed();
            return new YamlElement(this, 0);
        }
    }

    public void Dispose()
    {
        if (_rows is null) return;
        ArrayPool<DbRow>.Shared.Return(_rows);
        _rows = null;
        if (_pool is not null) { ArrayPool<byte>.Shared.Return(_pool); _pool = null; }
        if (_props is not null) { ArrayPool<NodeProps>.Shared.Return(_props); _props = null; }
        _owner?.Release();
        _owner = null;
        _data = default;
    }

    // ---------------------------------------------------------------------------------------------
    // Row access
    // ---------------------------------------------------------------------------------------------

    internal int RowCount => _rowCount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref DbRow Row(int i) => ref _rows![i];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint InfoOf(int i) => _rows![i].Info;

    /// <summary>Follows an alias row to its anchored node.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int ResolveAlias(int i)
    {
        ref DbRow r = ref _rows![i];
        return (r.Info & RowInfo.TypeMask) == RowInfo.Alias ? r.Location : i;
    }

    internal ReadOnlySpan<byte> ScalarBytes(int i)
    {
        ref DbRow r = ref _rows![i];
        return (r.Info & RowInfo.InPool) != 0
            ? new ReadOnlySpan<byte>(_pool, r.Location, r.Length)
            : _data.Span.Slice(r.Location, r.Length);
    }

    internal bool TryGetProps(int row, out NodeProps props)
    {
        NodeProps[]? arr = _props;
        if (arr is not null)
        {
            int lo = 0, hi = _propCount - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                int r = arr[mid].Row;
                if (r == row) { props = arr[mid]; return true; }
                if (r < row) lo = mid + 1; else hi = mid - 1;
            }
        }
        props = default;
        return false;
    }

    internal ReadOnlySpan<byte> DataSpan => _data.Span;

    /// <summary>
    /// Offset in the input of the first scalar of the subtree at <paramref name="row"/> whose text is still in the input
    /// (scalars rewritten into the pool, such as folded or escaped ones, have no input position), or -1.
    /// </summary>
    internal int FirstLocation(int row)
    {
        int last = row + Math.Max(1, _rows![row].Rows);
        for (int i = row; i < last && i < _rowCount; i++)
        {
            ref DbRow r = ref _rows![i];
            if ((r.Info & RowInfo.TypeMask) == RowInfo.Scalar && (r.Info & RowInfo.InPool) == 0) return r.Location;
        }
        return -1;
    }

    // ---------------------------------------------------------------------------------------------
    // Row construction (used by the builder)
    // ---------------------------------------------------------------------------------------------

    private void GrowRows()
    {
        DbRow[] old = _rows!;
        DbRow[] bigger = ArrayPool<DbRow>.Shared.Rent(old.Length * 2);
        Array.Copy(old, bigger, _rowCount);
        ArrayPool<DbRow>.Shared.Return(old);
        _rows = bigger;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int AddRow(uint info, int location, int length, int rows)
    {
        if (_rowCount == _rows!.Length) GrowRows();
        ref DbRow r = ref _rows[_rowCount];
        r.Location = location;
        r.Length = length;
        r.Rows = rows;
        r.Info = info;
        return _rowCount++;
    }

    private int CopyToPool(ReadOnlySpan<byte> value)
    {
        int need = _poolLength + value.Length;
        if (_pool is null || need > _pool.Length)
        {
            byte[] bigger = ArrayPool<byte>.Shared.Rent(Math.Max(need, Math.Max(1024, (_pool?.Length ?? 0) * 2)));
            if (_pool is not null)
            {
                Buffer.BlockCopy(_pool, 0, bigger, 0, _poolLength);
                ArrayPool<byte>.Shared.Return(_pool);
            }
            _pool = bigger;
        }
        int at = _poolLength;
        value.CopyTo(_pool.AsSpan(at));
        _poolLength = need;
        return at;
    }

    private void AddProps(int row, ref Utf8YamlReader reader)
    {
        if (_props is null)
        {
            _props = ArrayPool<NodeProps>.Shared.Rent(16);
        }
        else if (_propCount == _props.Length)
        {
            NodeProps[] bigger = ArrayPool<NodeProps>.Shared.Rent(_props.Length * 2);
            Array.Copy(_props, bigger, _propCount);
            ArrayPool<NodeProps>.Shared.Return(_props);
            _props = bigger;
        }

        ref NodeProps p = ref _props[_propCount++];
        p.Row = row;
        p.AnchorStart = reader.AnchorOffset;
        p.AnchorLength = reader.HasAnchor ? reader.AnchorLength : 0;
        if (reader.Tag != YamlTagKind.None)
        {
            p.PrefixKind = (byte)reader.TagPrefixKind;
            p.PrefixStart = reader.TagPrefixStart;
            p.PrefixLength = reader.TagPrefixLength;
            p.SuffixStart = reader.TagSuffixStart;
            p.SuffixLength = reader.TagSuffixLength;
            p.SuffixVerbatim = reader.TagSuffixVerbatim;
        }
        else
        {
            p.PrefixKind = 0;
            p.PrefixStart = p.PrefixLength = p.SuffixStart = p.SuffixLength = 0;
            p.SuffixVerbatim = false;
        }
    }

    internal string? GetAnchorName(int row)
    {
        if ((InfoOf(row) & RowInfo.HasProps) != 0 && TryGetProps(row, out NodeProps p) && p.AnchorLength > 0)
        {
            return Encoding.UTF8.GetString(_data.Span.Slice(p.AnchorStart, p.AnchorLength));
        }
        return null;
    }

    internal string? GetTagString(int row)
    {
        uint info = InfoOf(row);
        YamlTagKind kind = RowInfo.Tag(info);
        if (kind == YamlTagKind.None) return null;
        if (kind == YamlTagKind.NonSpecific) return "!";
        if (!TryGetProps(row, out NodeProps p)) return null;

        string prefix = p.PrefixKind switch
        {
            2 => "!",
            3 => "tag:yaml.org,2002:",
            1 => Encoding.UTF8.GetString(_data.Span.Slice(p.PrefixStart, p.PrefixLength)),
            _ => string.Empty,
        };
        ReadOnlySpan<byte> suffix = _data.Span.Slice(p.SuffixStart, p.SuffixLength);
        string s;
        if (p.SuffixVerbatim || suffix.IndexOf((byte)'%') < 0)
        {
            s = Encoding.UTF8.GetString(suffix);
        }
        else
        {
            Span<byte> buf = suffix.Length <= 256 ? stackalloc byte[suffix.Length] : new byte[suffix.Length];
            int n = 0;
            for (int i = 0; i < suffix.Length; i++)
            {
                if (suffix[i] == (byte)'%' && i + 2 < suffix.Length)
                {
                    buf[n++] = (byte)((Utf8YamlReader.HexVal(suffix[i + 1]) << 4) | Utf8YamlReader.HexVal(suffix[i + 2]));
                    i += 2;
                }
                else buf[n++] = suffix[i];
            }
            s = Encoding.UTF8.GetString(buf.Slice(0, n));
        }
        return prefix.Length == 0 ? s : prefix + s;
    }

    // ---------------------------------------------------------------------------------------------
    // Shared input buffer ownership (for ParseAll)
    // ---------------------------------------------------------------------------------------------

    internal sealed class DataOwner
    {
        private byte[]? _array;
        private int _refs;
        private readonly bool _pooled;

        public DataOwner(byte[] array, bool pooled = true)
        {
            _array = array;
            _pooled = pooled;
        }

        public byte[]? Array => _array;

        public void AddRef() => Interlocked.Increment(ref _refs);

        public void Release()
        {
            if (Interlocked.Decrement(ref _refs) == 0)
            {
                byte[]? a = Interlocked.Exchange(ref _array, null);
                if (a is not null && _pooled) ArrayPool<byte>.Shared.Return(a);
            }
        }
    }
}
