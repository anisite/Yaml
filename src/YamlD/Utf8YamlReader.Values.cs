namespace YamlD;

public ref partial struct Utf8YamlReader
{
    /// <summary>
    /// Decodes the current scalar (or alias name) to a string. Use <see cref="ResolveScalar"/> to find out whether
    /// a plain scalar means null, a boolean or a number.
    /// </summary>
    /// <exception cref="InvalidOperationException">The current event is not a scalar or alias.</exception>
    public readonly string GetString()
    {
        if (_tokenType is not (YamlTokenType.Scalar or YamlTokenType.Alias))
        {
            ThrowHelper.ThrowInvalidOperation("The current event is not a scalar or alias.");
        }
        ReadOnlySpan<byte> v = ValueSpan;
        return v.Length == 0 ? string.Empty : Encoding.UTF8.GetString(v);
    }

    /// <summary>Compares the current scalar value with UTF-8 text.</summary>
    public readonly bool ValueEquals(ReadOnlySpan<byte> utf8Text) => ValueSpan.SequenceEqual(utf8Text);

    /// <summary>Compares the current scalar value with a string.</summary>
    public readonly bool ValueEquals(string text)
    {
        ReadOnlySpan<byte> v = ValueSpan;
        int max = Encoding.UTF8.GetMaxByteCount(text.Length);
        Span<byte> buf = max <= 256 ? stackalloc byte[max] : new byte[max];
        int n = Encoding.UTF8.GetBytes(text, buf);
        return v.SequenceEqual(buf.Slice(0, n));
    }

    /// <summary>What the current scalar means under <paramref name="schema"/> (honours tag and style).</summary>
    public readonly YamlScalarKind ResolveScalar(YamlSchema schema = YamlSchema.Core)
    {
        if (_tokenType != YamlTokenType.Scalar)
        {
            ThrowHelper.ThrowInvalidOperation("The current event is not a scalar.");
        }
        return YamlScalar.Resolve(ValueSpan, _scalarStyle, _tagKind, schema);
    }

    /// <summary>Whether the current scalar is a null under <paramref name="schema"/>.</summary>
    public readonly bool IsNull(YamlSchema schema = YamlSchema.Core) =>
        _tokenType == YamlTokenType.Scalar && ResolveScalar(schema) == YamlScalarKind.Null;

    public readonly bool TryGetBoolean(out bool value, YamlSchema schema = YamlSchema.Core) =>
        YamlScalar.TryParseBoolean(ValueSpan, out value, schema);

    public readonly bool TryGetInt32(out int value, YamlSchema schema = YamlSchema.Core)
    {
        if (YamlScalar.TryParseInt64(ValueSpan, out long l, schema) && l >= int.MinValue && l <= int.MaxValue)
        {
            value = (int)l;
            return true;
        }
        value = 0;
        return false;
    }

    public readonly bool TryGetInt64(out long value, YamlSchema schema = YamlSchema.Core) =>
        YamlScalar.TryParseInt64(ValueSpan, out value, schema);

    public readonly bool TryGetUInt64(out ulong value, YamlSchema schema = YamlSchema.Core) =>
        YamlScalar.TryParseUInt64(ValueSpan, out value, schema);

    public readonly bool TryGetDouble(out double value, YamlSchema schema = YamlSchema.Core) =>
        YamlScalar.TryParseDouble(ValueSpan, out value, schema);

    public readonly bool GetBoolean(YamlSchema schema = YamlSchema.Core)
    {
        if (!TryGetBoolean(out bool v, schema)) ThrowHelper.ThrowFormat("The scalar is not a valid boolean.");
        return v;
    }

    public readonly int GetInt32(YamlSchema schema = YamlSchema.Core)
    {
        if (!TryGetInt32(out int v, schema)) ThrowHelper.ThrowFormat("The scalar is not a valid 32-bit integer.");
        return v;
    }

    public readonly long GetInt64(YamlSchema schema = YamlSchema.Core)
    {
        if (!TryGetInt64(out long v, schema)) ThrowHelper.ThrowFormat("The scalar is not a valid 64-bit integer.");
        return v;
    }

    public readonly double GetDouble(YamlSchema schema = YamlSchema.Core)
    {
        if (!TryGetDouble(out double v, schema)) ThrowHelper.ThrowFormat("The scalar is not a valid floating-point number.");
        return v;
    }

    // ---- internal accessors used by the DOM builder ----------------------------------------------

    internal readonly bool ValueIsInInput => !_valueInScratch;
    internal readonly int ValueOffset => _valueStart;
    internal readonly int AnchorOffset => _anchorStart;
    internal readonly int AnchorLength => _anchorEnd - _anchorStart;
    internal readonly int TagPrefixKind => _tagPrefixKind;
    internal readonly int TagPrefixStart => _tagPrefixStart;
    internal readonly int TagPrefixLength => _tagPrefixEnd - _tagPrefixStart;
    internal readonly int TagSuffixStart => _tagSuffixStart;
    internal readonly int TagSuffixLength => _tagSuffixEnd - _tagSuffixStart;
    internal readonly bool TagSuffixVerbatim => _tagSuffixVerbatim;
}
