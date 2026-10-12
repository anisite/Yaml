using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace System.Text.Yaml;

/// <summary>A key/value pair of a YAML mapping.</summary>
public readonly struct YamlEntry
{
    public YamlElement Key { get; }
    public YamlElement Value { get; }

    internal YamlEntry(YamlElement key, YamlElement value)
    {
        Key = key;
        Value = value;
    }

    public void Deconstruct(out YamlElement key, out YamlElement value)
    {
        key = Key;
        value = Value;
    }
}

/// <summary>
/// A lightweight, read-only view of a node in a <see cref="YamlDocument"/> (the counterpart of <c>JsonElement</c>).
/// Aliases are transparent: an element obtained through an alias behaves exactly like the anchored node.
/// </summary>
public readonly struct YamlElement
{
    private readonly YamlDocument _doc;
    private readonly int _idx;

    internal YamlElement(YamlDocument doc, int idx)
    {
        _doc = doc;
        _idx = doc.ResolveAlias(idx);
    }

    private uint Info => _doc.InfoOf(_idx);

    private YamlDocument Doc
    {
        get
        {
            if (_doc is null) ThrowHelper.ThrowInvalidOperation("The element is not initialized.");
            return _doc;
        }
    }

    /// <summary>Resolved kind of the node (core schema unless the document was parsed with another schema).</summary>
    public YamlValueKind ValueKind
    {
        get
        {
            if (_doc is null) return YamlValueKind.Undefined;
            uint info = Info;
            switch (RowInfo.Type(info))
            {
                case RowInfo.Mapping: return YamlValueKind.Mapping;
                case RowInfo.Sequence: return YamlValueKind.Sequence;
                default:
                    return _doc.ScalarKindOf(_idx) switch
                    {
                        YamlScalarKind.Null => YamlValueKind.Null,
                        YamlScalarKind.Boolean => YamlValueKind.Boolean,
                        YamlScalarKind.Integer => YamlValueKind.Integer,
                        YamlScalarKind.Float => YamlValueKind.Float,
                        _ => YamlValueKind.String,
                    };
            }
        }
    }

    public bool IsNull => ValueKind == YamlValueKind.Null;

    /// <summary>Presentation style of a scalar node.</summary>
    public YamlScalarStyle ScalarStyle => RowInfo.Style(Doc.InfoOf(_idx));

    /// <summary>Block or flow presentation of a collection.</summary>
    public YamlCollectionStyle CollectionStyle => (Doc.InfoOf(_idx) & RowInfo.Flow) != 0 ? YamlCollectionStyle.Flow : YamlCollectionStyle.Block;

    /// <summary>Classification of the node's tag.</summary>
    public YamlTagKind TagKind => RowInfo.Tag(Doc.InfoOf(_idx));

    /// <summary>The full tag URI, or <c>null</c> when the node has no tag.</summary>
    public string? Tag => Doc.GetTagString(_idx);

    /// <summary>The anchor name defined on this node, or <c>null</c>.</summary>
    public string? Anchor => Doc.GetAnchorName(_idx);

    // ---------------------------------------------------------------------------------------------
    // Collections
    // ---------------------------------------------------------------------------------------------

    /// <summary>Number of items of a sequence, or of effective entries of a mapping (merge keys expanded).</summary>
    public int Count
    {
        get
        {
            uint info = Doc.InfoOf(_idx);
            uint type = RowInfo.Type(info);
            if (type == RowInfo.Sequence) return _doc.Row(_idx).Length;
            if (type == RowInfo.Mapping)
            {
                if ((info & RowInfo.HasMerge) != 0) return _doc.EffectivePairs(_idx).Count >> 1;
                return _doc.Row(_idx).Length;
            }
            ThrowHelper.ThrowInvalidOperation("The element is not a collection.");
            return 0;
        }
    }

    /// <summary>Gets a sequence item.</summary>
    public YamlElement this[int index]
    {
        get
        {
            YamlDocument d = Doc;
            uint info = d.InfoOf(_idx);
            if (RowInfo.Type(info) != RowInfo.Sequence) ThrowHelper.ThrowInvalidOperation("The element is not a sequence.");
            ref DbRow row = ref d.Row(_idx);
            if ((uint)index >= (uint)row.Length) ThrowHelper.ThrowArgumentOutOfRange(nameof(index));

            int child = _idx + 1;
            if ((info & RowInfo.Complex) != 0)
            {
                for (int i = 0; i < index; i++) child += d.Row(child).Rows;
            }
            else
            {
                child += index;
            }
            return new YamlElement(d, child);
        }
    }

    /// <summary>Gets the value of a mapping entry whose scalar key has the given text.</summary>
    public YamlElement this[string key]
    {
        get
        {
            if (!TryGetProperty(key, out YamlElement v)) ThrowHelper.ThrowKeyNotFound(key);
            return v;
        }
    }

    /// <summary>Gets the value of a mapping entry whose scalar key equals the given UTF-8 text.</summary>
    public YamlElement this[ReadOnlySpan<byte> utf8Key]
    {
        get
        {
            if (!TryGetProperty(utf8Key, out YamlElement v)) ThrowHelper.ThrowKeyNotFound(Encoding.UTF8.GetString(utf8Key));
            return v;
        }
    }

    public bool TryGetProperty(string name, out YamlElement value)
    {
        ArgumentNullException.ThrowIfNull(name);
        int max = Encoding.UTF8.GetMaxByteCount(name.Length);
        Span<byte> buf = max <= 256 ? stackalloc byte[max] : new byte[max];
        int n = Encoding.UTF8.GetBytes(name, buf);
        return TryGetProperty(buf.Slice(0, n), out value);
    }

    public bool TryGetProperty(ReadOnlySpan<char> name, out YamlElement value)
    {
        int max = Encoding.UTF8.GetMaxByteCount(name.Length);
        Span<byte> buf = max <= 256 ? stackalloc byte[max] : new byte[max];
        int n = Encoding.UTF8.GetBytes(name, buf);
        return TryGetProperty(buf.Slice(0, n), out value);
    }

    public bool TryGetProperty(ReadOnlySpan<byte> utf8Name, out YamlElement value)
    {
        YamlDocument d = Doc;
        if (RowInfo.Type(d.InfoOf(_idx)) != RowInfo.Mapping) ThrowHelper.ThrowInvalidOperation("The element is not a mapping.");
        if (d.TryFindValue(_idx, utf8Name, out int row))
        {
            value = new YamlElement(d, row);
            return true;
        }
        value = default;
        return false;
    }

    public SequenceEnumerator EnumerateSequence()
    {
        YamlDocument d = Doc;
        if (RowInfo.Type(d.InfoOf(_idx)) != RowInfo.Sequence) ThrowHelper.ThrowInvalidOperation("The element is not a sequence.");
        return new SequenceEnumerator(d, _idx);
    }

    public MappingEnumerator EnumerateMapping()
    {
        YamlDocument d = Doc;
        uint info = d.InfoOf(_idx);
        if (RowInfo.Type(info) != RowInfo.Mapping) ThrowHelper.ThrowInvalidOperation("The element is not a mapping.");
        return new MappingEnumerator(d, _idx, (info & RowInfo.HasMerge) != 0);
    }

    /// <summary>Enumerates a sequence; allocation-free.</summary>
    public struct SequenceEnumerator : IEnumerable<YamlElement>, IEnumerator<YamlElement>
    {
        private readonly YamlDocument _doc;
        private int _next;
        private int _remaining;
        private int _current;

        internal SequenceEnumerator(YamlDocument doc, int seq)
        {
            _doc = doc;
            _next = seq + 1;
            _remaining = doc.Row(seq).Length;
            _current = -1;
        }

        public readonly YamlElement Current => new(_doc, _current);
        readonly object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_remaining == 0) return false;
            _remaining--;
            _current = _next;
            _next += _doc.Row(_next).Rows;
            return true;
        }

        public readonly SequenceEnumerator GetEnumerator() => this;
        readonly IEnumerator<YamlElement> IEnumerable<YamlElement>.GetEnumerator() => this;
        readonly IEnumerator IEnumerable.GetEnumerator() => this;
        public void Reset() => throw new NotSupportedException();
        public readonly void Dispose() { }
    }

    /// <summary>Enumerates the (effective) entries of a mapping.</summary>
    public struct MappingEnumerator : IEnumerable<YamlEntry>, IEnumerator<YamlEntry>
    {
        private readonly YamlDocument _doc;
        private readonly List<int>? _effective;
        private int _next;
        private int _remaining;
        private int _key;
        private int _value;
        private int _pos;

        internal MappingEnumerator(YamlDocument doc, int map, bool hasMerge)
        {
            _doc = doc;
            _key = _value = -1;
            _pos = 0;
            if (hasMerge)
            {
                _effective = doc.EffectivePairs(map);
                _remaining = _effective.Count >> 1;
                _next = 0;
            }
            else
            {
                _effective = null;
                _remaining = doc.Row(map).Length;
                _next = map + 1;
            }
        }

        public readonly YamlEntry Current => new(new YamlElement(_doc, _key), new YamlElement(_doc, _value));
        readonly object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_remaining == 0) return false;
            _remaining--;
            if (_effective is not null)
            {
                _key = _effective[_pos++];
                _value = _effective[_pos++];
            }
            else
            {
                _key = _next;
                _value = _key + _doc.Row(_key).Rows;
                _next = _value + _doc.Row(_value).Rows;
            }
            return true;
        }

        public readonly MappingEnumerator GetEnumerator() => this;
        readonly IEnumerator<YamlEntry> IEnumerable<YamlEntry>.GetEnumerator() => this;
        readonly IEnumerator IEnumerable.GetEnumerator() => this;
        public void Reset() => throw new NotSupportedException();
        public readonly void Dispose() { }
    }

    // ---------------------------------------------------------------------------------------------
    // Scalars
    // ---------------------------------------------------------------------------------------------

    private void RequireScalar()
    {
        if (RowInfo.Type(Doc.InfoOf(_idx)) != RowInfo.Scalar)
        {
            ThrowHelper.ThrowInvalidOperation("The element is not a scalar.");
        }
    }

    /// <summary>The decoded UTF-8 bytes of a scalar; valid until the document is disposed.</summary>
    public ReadOnlySpan<byte> GetUtf8Span()
    {
        RequireScalar();
        return _doc.ScalarBytes(_idx);
    }

    /// <summary>
    /// The scalar's text, or <c>null</c> when it resolves to null (<c>~</c>, <c>null</c>, empty plain scalar).
    /// Numbers and booleans are returned as written.
    /// </summary>
    public string? GetString()
    {
        RequireScalar();
        if (_doc.ScalarKindOf(_idx) == YamlScalarKind.Null) return null;
        ReadOnlySpan<byte> v = _doc.ScalarBytes(_idx);
        return v.Length == 0 ? string.Empty : Encoding.UTF8.GetString(v);
    }

    public bool ValueEquals(ReadOnlySpan<byte> utf8Text)
    {
        RequireScalar();
        return _doc.ScalarBytes(_idx).SequenceEqual(utf8Text);
    }

    public bool ValueEquals(string text)
    {
        RequireScalar();
        int max = Encoding.UTF8.GetMaxByteCount(text.Length);
        Span<byte> buf = max <= 256 ? stackalloc byte[max] : new byte[max];
        int n = Encoding.UTF8.GetBytes(text, buf);
        return _doc.ScalarBytes(_idx).SequenceEqual(buf.Slice(0, n));
    }

    public bool TryGetBoolean(out bool value)
    {
        RequireScalar();
        return YamlScalar.TryParseBoolean(_doc.ScalarBytes(_idx), out value, _doc.Schema);
    }

    public bool TryGetInt32(out int value)
    {
        RequireScalar();
        if (YamlScalar.TryParseInt64(_doc.ScalarBytes(_idx), out long l, _doc.Schema) && l >= int.MinValue && l <= int.MaxValue)
        {
            value = (int)l;
            return true;
        }
        value = 0;
        return false;
    }

    public bool TryGetInt64(out long value)
    {
        RequireScalar();
        return YamlScalar.TryParseInt64(_doc.ScalarBytes(_idx), out value, _doc.Schema);
    }

    public bool TryGetUInt64(out ulong value)
    {
        RequireScalar();
        return YamlScalar.TryParseUInt64(_doc.ScalarBytes(_idx), out value, _doc.Schema);
    }

    public bool TryGetDouble(out double value)
    {
        RequireScalar();
        return YamlScalar.TryParseDouble(_doc.ScalarBytes(_idx), out value, _doc.Schema);
    }

    public bool TryGetDecimal(out decimal value)
    {
        RequireScalar();
        return decimal.TryParse(_doc.ScalarBytes(_idx), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public bool GetBoolean()
    {
        if (!TryGetBoolean(out bool v)) ThrowHelper.ThrowFormat("The scalar is not a valid boolean.");
        return v;
    }

    public int GetInt32()
    {
        if (!TryGetInt32(out int v)) ThrowHelper.ThrowFormat("The scalar is not a valid 32-bit integer.");
        return v;
    }

    public long GetInt64()
    {
        if (!TryGetInt64(out long v)) ThrowHelper.ThrowFormat("The scalar is not a valid 64-bit integer.");
        return v;
    }

    public ulong GetUInt64()
    {
        if (!TryGetUInt64(out ulong v)) ThrowHelper.ThrowFormat("The scalar is not a valid unsigned 64-bit integer.");
        return v;
    }

    public double GetDouble()
    {
        if (!TryGetDouble(out double v)) ThrowHelper.ThrowFormat("The scalar is not a valid floating-point number.");
        return v;
    }

    public decimal GetDecimal()
    {
        if (!TryGetDecimal(out decimal v)) ThrowHelper.ThrowFormat("The scalar is not a valid decimal.");
        return v;
    }

    /// <summary>Decodes a base64 scalar (the <c>!!binary</c> type); whitespace and line breaks are ignored.</summary>
    public byte[] GetBytesFromBase64()
    {
        RequireScalar();
        ReadOnlySpan<byte> v = _doc.ScalarBytes(_idx);
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(v.Length);
        try
        {
            int n = 0;
            foreach (byte b in v)
            {
                if (b != (byte)' ' && b != (byte)'\t' && b != (byte)'\n' && b != (byte)'\r') rented[n++] = b;
            }
            byte[] result = new byte[System.Buffers.Text.Base64.GetMaxDecodedFromUtf8Length(n)];
            if (System.Buffers.Text.Base64.DecodeFromUtf8(rented.AsSpan(0, n), result, out _, out int written) != System.Buffers.OperationStatus.Done)
            {
                ThrowHelper.ThrowFormat("The scalar is not valid base64.");
            }
            return written == result.Length ? result : result.AsSpan(0, written).ToArray();
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Scalar text, or a short description for collections.</summary>
    public override string ToString()
    {
        if (_doc is null) return string.Empty;
        uint type = RowInfo.Type(Info);
        if (type == RowInfo.Scalar)
        {
            ReadOnlySpan<byte> v = _doc.ScalarBytes(_idx);
            return v.Length == 0 ? string.Empty : Encoding.UTF8.GetString(v);
        }
        return type == RowInfo.Mapping ? $"Mapping[{Count}]" : $"Sequence[{Count}]";
    }

    // ---------------------------------------------------------------------------------------------
    // JSON
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Writes this node as JSON. Mapping keys must be scalars (their text becomes the property name); non-finite
    /// floats are written as strings. Aliases are expanded; <paramref name="maxNodes"/> bounds the amount of
    /// output so alias bombs cannot exhaust memory.
    /// </summary>
    public void WriteTo(Utf8JsonWriter writer, long maxNodes = 10_000_000)
    {
        ArgumentNullException.ThrowIfNull(writer);
        long budget = maxNodes;
        WriteCore(writer, ref budget);
    }

    /// <summary>Renders this node as a JSON string.</summary>
    public string ToJsonString()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            WriteTo(w);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private void WriteCore(Utf8JsonWriter w, ref long budget)
    {
        if (--budget < 0) ThrowHelper.ThrowInvalidOperation("The node expansion limit was exceeded while converting to JSON.");
        switch (ValueKind)
        {
            case YamlValueKind.Mapping:
                w.WriteStartObject();
                foreach (YamlEntry e in EnumerateMapping())
                {
                    if (RowInfo.Type(e.Key.Info) != RowInfo.Scalar)
                    {
                        ThrowHelper.ThrowInvalidOperation("A mapping with a non-scalar key cannot be represented as JSON.");
                    }
                    w.WritePropertyName(e.Key._doc.ScalarBytes(e.Key._idx));
                    e.Value.WriteCore(w, ref budget);
                }
                w.WriteEndObject();
                break;
            case YamlValueKind.Sequence:
                w.WriteStartArray();
                foreach (YamlElement item in EnumerateSequence())
                {
                    item.WriteCore(w, ref budget);
                }
                w.WriteEndArray();
                break;
            case YamlValueKind.Null:
                w.WriteNullValue();
                break;
            case YamlValueKind.Boolean:
                w.WriteBooleanValue(GetBoolean());
                break;
            case YamlValueKind.Integer:
                if (TryGetInt64(out long l)) w.WriteNumberValue(l);
                else if (TryGetUInt64(out ulong u)) w.WriteNumberValue(u);
                else if (TryGetDouble(out double dd)) w.WriteNumberValue(dd);
                else w.WriteStringValue(_doc.ScalarBytes(_idx));
                break;
            case YamlValueKind.Float:
                if (TryGetDouble(out double d) && double.IsFinite(d)) w.WriteNumberValue(d);
                else w.WriteStringValue(_doc.ScalarBytes(_idx));
                break;
            default:
                w.WriteStringValue(_doc.ScalarBytes(_idx));
                break;
        }
    }
}
