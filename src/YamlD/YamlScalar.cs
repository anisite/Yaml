using System.Globalization;
using System.Runtime.CompilerServices;

namespace YamlD;

/// <summary>
/// Scalar resolution and conversion following the YAML 1.2.2 schemas (chapter 10): failsafe, JSON and core,
/// plus an opt-in YAML 1.1 flavour.
/// </summary>
public static class YamlScalar
{
    /// <summary>
    /// Determines what an (already decoded) scalar means under <paramref name="schema"/>.
    /// An explicit tag always wins over the content; quoted and block scalars are strings.
    /// </summary>
    public static YamlScalarKind Resolve(ReadOnlySpan<byte> value, YamlScalarStyle style, YamlTagKind tag = YamlTagKind.None, YamlSchema schema = YamlSchema.Core)
    {
        switch (tag)
        {
            case YamlTagKind.Str:
            case YamlTagKind.NonSpecific:
            case YamlTagKind.Other:
            case YamlTagKind.Merge:
                return YamlScalarKind.String;
            case YamlTagKind.Int: return YamlScalarKind.Integer;
            case YamlTagKind.Float: return YamlScalarKind.Float;
            case YamlTagKind.Bool: return YamlScalarKind.Boolean;
            case YamlTagKind.Null: return YamlScalarKind.Null;
        }

        if (style != YamlScalarStyle.Plain) return YamlScalarKind.String;
        return ResolvePlain(value, schema);
    }

    /// <summary>Resolves an untagged plain scalar.</summary>
    public static YamlScalarKind ResolvePlain(ReadOnlySpan<byte> v, YamlSchema schema = YamlSchema.Core)
    {
        if (schema == YamlSchema.Failsafe) return YamlScalarKind.String;
        if (v.Length == 0) return YamlScalarKind.Null;

        byte c = v[0];
        if (schema == YamlSchema.Json && (c == (byte)'~' || (c >= (byte)'A' && c <= (byte)'Z')))
        {
            // JSON schema: only the lowercase literals null / true / false.
            return YamlScalarKind.String;
        }
        switch (c)
        {
            case (byte)'~':
                return v.Length == 1 ? YamlScalarKind.Null : YamlScalarKind.String;

            case (byte)'n':
            case (byte)'N':
                if (IsNull(v)) return YamlScalarKind.Null;
                if (schema == YamlSchema.Yaml11 && (v.Length == 1 || EqualsAny(v, "no"u8, "No"u8, "NO"u8)))
                    return YamlScalarKind.Boolean;
                return YamlScalarKind.String;

            case (byte)'t':
            case (byte)'T':
                return IsTrue(v) ? YamlScalarKind.Boolean : YamlScalarKind.String;

            case (byte)'f':
            case (byte)'F':
                return IsFalse(v) ? YamlScalarKind.Boolean : YamlScalarKind.String;

            case (byte)'y':
            case (byte)'Y':
                if (schema == YamlSchema.Yaml11 && (v.Length == 1 || EqualsAny(v, "yes"u8, "Yes"u8, "YES"u8)))
                    return YamlScalarKind.Boolean;
                return YamlScalarKind.String;

            case (byte)'o':
            case (byte)'O':
                if (schema == YamlSchema.Yaml11 && EqualsAny(v, "on"u8, "On"u8, "ON"u8, "off"u8, "Off"u8, "OFF"u8))
                    return YamlScalarKind.Boolean;
                return YamlScalarKind.String;

            case (byte)'<':
                return YamlScalarKind.String;

            default:
                if ((c >= (byte)'0' && c <= (byte)'9') || c == (byte)'-' || c == (byte)'+' || c == (byte)'.')
                {
                    return schema switch
                    {
                        YamlSchema.Json => ResolveJsonNumber(v),
                        YamlSchema.Yaml11 => ResolveYaml11Number(v),
                        _ => ResolveCoreNumber(v),
                    };
                }
                return YamlScalarKind.String;
        }
    }

    private static bool EqualsAny(ReadOnlySpan<byte> v, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c = default, ReadOnlySpan<byte> d = default, ReadOnlySpan<byte> e = default, ReadOnlySpan<byte> f = default)
    {
        return v.SequenceEqual(a) || v.SequenceEqual(b)
            || (!c.IsEmpty && v.SequenceEqual(c)) || (!d.IsEmpty && v.SequenceEqual(d))
            || (!e.IsEmpty && v.SequenceEqual(e)) || (!f.IsEmpty && v.SequenceEqual(f));
    }

    private static bool IsNull(ReadOnlySpan<byte> v) =>
        v.Length == 4 && (v.SequenceEqual("null"u8) || v.SequenceEqual("Null"u8) || v.SequenceEqual("NULL"u8));

    private static bool IsTrue(ReadOnlySpan<byte> v) =>
        v.Length == 4 && (v.SequenceEqual("true"u8) || v.SequenceEqual("True"u8) || v.SequenceEqual("TRUE"u8));

    private static bool IsFalse(ReadOnlySpan<byte> v) =>
        v.Length == 5 && (v.SequenceEqual("false"u8) || v.SequenceEqual("False"u8) || v.SequenceEqual("FALSE"u8));

    // ---- core schema numbers ----------------------------------------------------------------------

    private static YamlScalarKind ResolveCoreNumber(ReadOnlySpan<byte> v)
    {
        int i = 0;
        byte c = v[0];

        if (c == (byte)'0' && v.Length > 2)
        {
            if (v[1] == (byte)'x') return AllHex(v.Slice(2)) ? YamlScalarKind.Integer : YamlScalarKind.String;
            if (v[1] == (byte)'o') return AllOctal(v.Slice(2)) ? YamlScalarKind.Integer : YamlScalarKind.String;
        }

        if (c == (byte)'-' || c == (byte)'+')
        {
            i = 1;
            if (v.Length == 1) return YamlScalarKind.String;
        }

        // [-+]?\.(inf|Inf|INF)  and  \.(nan|NaN|NAN)
        if (v[i] == (byte)'.')
        {
            ReadOnlySpan<byte> rest = v.Slice(i + 1);
            if (rest.Length == 3)
            {
                if (rest.SequenceEqual("inf"u8) || rest.SequenceEqual("Inf"u8) || rest.SequenceEqual("INF"u8)) return YamlScalarKind.Float;
                if (i == 0 && (rest.SequenceEqual("nan"u8) || rest.SequenceEqual("NaN"u8) || rest.SequenceEqual("NAN"u8))) return YamlScalarKind.Float;
            }
        }

        int digits = 0;
        int start = i;
        while (i < v.Length && (uint)(v[i] - (byte)'0') <= 9) { i++; digits++; }
        if (i == v.Length) return digits > 0 ? YamlScalarKind.Integer : YamlScalarKind.String;

        bool isFloat = false;
        int fracDigits = 0;
        if (v[i] == (byte)'.')
        {
            isFloat = true;
            i++;
            while (i < v.Length && (uint)(v[i] - (byte)'0') <= 9) { i++; fracDigits++; }
        }
        if (digits == 0 && fracDigits == 0) return YamlScalarKind.String;
        _ = start;

        if (i < v.Length && (v[i] == (byte)'e' || v[i] == (byte)'E'))
        {
            isFloat = true;
            i++;
            if (i < v.Length && (v[i] == (byte)'-' || v[i] == (byte)'+')) i++;
            int expDigits = 0;
            while (i < v.Length && (uint)(v[i] - (byte)'0') <= 9) { i++; expDigits++; }
            if (expDigits == 0) return YamlScalarKind.String;
        }

        if (i != v.Length) return YamlScalarKind.String;
        return isFloat ? YamlScalarKind.Float : YamlScalarKind.Integer;
    }

    // ---- JSON schema numbers ------------------------------------------------------------------------

    private static YamlScalarKind ResolveJsonNumber(ReadOnlySpan<byte> v)
    {
        int i = 0;
        if (v[0] == (byte)'-') i = 1;
        if (i >= v.Length) return YamlScalarKind.String;
        if (v[i] == (byte)'0')
        {
            i++;
        }
        else if (v[i] >= (byte)'1' && v[i] <= (byte)'9')
        {
            while (i < v.Length && (uint)(v[i] - (byte)'0') <= 9) i++;
        }
        else
        {
            return YamlScalarKind.String;
        }

        if (i == v.Length) return YamlScalarKind.Integer;
        bool isFloat = false;
        if (v[i] == (byte)'.')
        {
            isFloat = true;
            i++;
            while (i < v.Length && (uint)(v[i] - (byte)'0') <= 9) i++;
        }
        if (i < v.Length && (v[i] == (byte)'e' || v[i] == (byte)'E'))
        {
            isFloat = true;
            i++;
            if (i < v.Length && (v[i] == (byte)'-' || v[i] == (byte)'+')) i++;
            int exp = 0;
            while (i < v.Length && (uint)(v[i] - (byte)'0') <= 9) { i++; exp++; }
            if (exp == 0) return YamlScalarKind.String;
        }
        if (i != v.Length) return YamlScalarKind.String;
        return isFloat ? YamlScalarKind.Float : YamlScalarKind.Integer;
    }

    // ---- YAML 1.1 numbers ---------------------------------------------------------------------------

    private static YamlScalarKind ResolveYaml11Number(ReadOnlySpan<byte> v)
    {
        if (TryParseInt64Yaml11(v, out _, out bool overflow) || overflow) return YamlScalarKind.Integer;
        if (ResolveCoreNumber(v) != YamlScalarKind.String)
        {
            return ResolveCoreNumber(v);
        }
        // 1.1 floats may contain '_' separators and sexagesimal parts (190:20:30.15)
        if (TryParseDoubleYaml11(v, out _)) return YamlScalarKind.Float;
        return YamlScalarKind.String;
    }

    private static bool AllHex(ReadOnlySpan<byte> s)
    {
        if (s.Length == 0) return false;
        foreach (byte b in s)
        {
            if (!((b >= (byte)'0' && b <= (byte)'9') || ((b | 0x20) >= (byte)'a' && (b | 0x20) <= (byte)'f'))) return false;
        }
        return true;
    }

    private static bool AllOctal(ReadOnlySpan<byte> s)
    {
        if (s.Length == 0) return false;
        foreach (byte b in s)
        {
            if (b < (byte)'0' || b > (byte)'7') return false;
        }
        return true;
    }

    // ---- conversions ------------------------------------------------------------------------------

    /// <summary>Parses a boolean (<c>true</c>/<c>false</c> in any core-schema casing; YAML 1.1 literals too if requested).</summary>
    public static bool TryParseBoolean(ReadOnlySpan<byte> v, out bool value, YamlSchema schema = YamlSchema.Core)
    {
        if (IsTrue(v)) { value = true; return true; }
        if (IsFalse(v)) { value = false; return true; }
        if (schema == YamlSchema.Yaml11)
        {
            if (EqualsAny(v, "y"u8, "Y"u8, "yes"u8, "Yes"u8, "YES"u8, "on"u8) || EqualsAny(v, "On"u8, "ON"u8))
            { value = true; return true; }
            if (EqualsAny(v, "n"u8, "N"u8, "no"u8, "No"u8, "NO"u8, "off"u8) || EqualsAny(v, "Off"u8, "OFF"u8))
            { value = false; return true; }
        }
        value = false;
        return false;
    }

    /// <summary>Parses an integer (decimal, <c>0o</c> octal, <c>0x</c> hexadecimal; YAML 1.1 forms with <paramref name="schema"/>).</summary>
    public static bool TryParseInt64(ReadOnlySpan<byte> v, out long value, YamlSchema schema = YamlSchema.Core)
    {
        if (schema == YamlSchema.Yaml11)
        {
            return TryParseInt64Yaml11(v, out value, out _);
        }

        value = 0;
        if (v.Length == 0) return false;

        int i = 0;
        bool neg = false;
        if (v[0] == (byte)'-') { neg = true; i = 1; }
        else if (v[0] == (byte)'+') i = 1;
        if (i >= v.Length) return false;

        ulong acc = 0;
        if (v.Length - i > 2 && v[i] == (byte)'0' && (v[i + 1] == (byte)'x' || v[i + 1] == (byte)'o'))
        {
            if (i != 0) return false; // no sign allowed on 0x / 0o forms
            bool hex = v[1] == (byte)'x';
            for (int k = 2; k < v.Length; k++)
            {
                int d;
                byte b = v[k];
                if (hex)
                {
                    if (b >= (byte)'0' && b <= (byte)'9') d = b - '0';
                    else if ((b | 0x20) >= (byte)'a' && (b | 0x20) <= (byte)'f') d = (b | 0x20) - 'a' + 10;
                    else return false;
                    if (acc > (ulong.MaxValue >> 4)) return false;
                    acc = (acc << 4) | (uint)d;
                }
                else
                {
                    if (b < (byte)'0' || b > (byte)'7') return false;
                    if (acc > (ulong.MaxValue >> 3)) return false;
                    acc = (acc << 3) | (uint)(b - '0');
                }
            }
            if (acc > long.MaxValue) return false;
            value = (long)acc;
            return true;
        }

        for (; i < v.Length; i++)
        {
            uint d = (uint)(v[i] - (byte)'0');
            if (d > 9) return false;
            if (acc > (ulong.MaxValue - d) / 10) return false;
            acc = acc * 10 + d;
        }
        if (neg)
        {
            if (acc > (ulong)long.MaxValue + 1) return false;
            value = (long)(0 - acc);
            return true;
        }
        if (acc > long.MaxValue) return false;
        value = (long)acc;
        return true;
    }

    /// <summary>Parses an unsigned integer.</summary>
    public static bool TryParseUInt64(ReadOnlySpan<byte> v, out ulong value, YamlSchema schema = YamlSchema.Core)
    {
        value = 0;
        if (schema != YamlSchema.Yaml11 && v.Length > 0 && v[0] != (byte)'-')
        {
            int i = v[0] == (byte)'+' ? 1 : 0;
            if (i < v.Length)
            {
                ulong acc = 0;
                int k = i;
                if (v.Length - i > 2 && v[i] == (byte)'0' && (v[i + 1] == (byte)'x' || v[i + 1] == (byte)'o') && i == 0)
                {
                    bool hex = v[1] == (byte)'x';
                    for (k = 2; k < v.Length; k++)
                    {
                        byte b = v[k];
                        if (hex)
                        {
                            int d;
                            if (b >= (byte)'0' && b <= (byte)'9') d = b - '0';
                            else if ((b | 0x20) >= (byte)'a' && (b | 0x20) <= (byte)'f') d = (b | 0x20) - 'a' + 10;
                            else return false;
                            if (acc > (ulong.MaxValue >> 4)) return false;
                            acc = (acc << 4) | (uint)d;
                        }
                        else
                        {
                            if (b < (byte)'0' || b > (byte)'7') return false;
                            if (acc > (ulong.MaxValue >> 3)) return false;
                            acc = (acc << 3) | (uint)(b - '0');
                        }
                    }
                    value = acc;
                    return true;
                }
                for (; k < v.Length; k++)
                {
                    uint d = (uint)(v[k] - (byte)'0');
                    if (d > 9) return false;
                    if (acc > (ulong.MaxValue - d) / 10) return false;
                    acc = acc * 10 + d;
                }
                value = acc;
                return true;
            }
            return false;
        }
        if (TryParseInt64(v, out long l, schema) && l >= 0)
        {
            value = (ulong)l;
            return true;
        }
        return false;
    }

    /// <summary>Parses a floating-point number, including <c>.inf</c>, <c>-.inf</c> and <c>.nan</c>.</summary>
    public static bool TryParseDouble(ReadOnlySpan<byte> v, out double value, YamlSchema schema = YamlSchema.Core)
    {
        value = 0;
        if (v.Length == 0) return false;

        int i = (v[0] == (byte)'-' || v[0] == (byte)'+') ? 1 : 0;
        if (v.Length - i == 4 && v[i] == (byte)'.')
        {
            ReadOnlySpan<byte> w = v.Slice(i + 1);
            if (w.SequenceEqual("inf"u8) || w.SequenceEqual("Inf"u8) || w.SequenceEqual("INF"u8))
            {
                value = v[0] == (byte)'-' ? double.NegativeInfinity : double.PositiveInfinity;
                return true;
            }
            if (i == 0 && (w.SequenceEqual("nan"u8) || w.SequenceEqual("NaN"u8) || w.SequenceEqual("NAN"u8)))
            {
                value = double.NaN;
                return true;
            }
        }

        if (schema == YamlSchema.Yaml11)
        {
            if (TryParseDoubleYaml11(v, out value)) return true;
        }

        // Integers (including 0x / 0o forms) are valid floats as well.
        if (ResolveCoreNumber(v) == YamlScalarKind.Integer && (v.Length < 3 || v[1] != (byte)'x' && v[1] != (byte)'o'))
        {
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
        if (v.Length > 2 && v[0] == (byte)'0' && (v[1] == (byte)'x' || v[1] == (byte)'o'))
        {
            if (TryParseInt64(v, out long l, schema)) { value = l; return true; }
            if (TryParseUInt64(v, out ulong u, schema)) { value = u; return true; }
            return false;
        }

        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    // ---- YAML 1.1 helpers ---------------------------------------------------------------------------

    private static bool TryParseInt64Yaml11(ReadOnlySpan<byte> v, out long value, out bool overflow)
    {
        value = 0;
        overflow = false;
        if (v.Length == 0) return false;

        int i = 0;
        bool neg = false;
        if (v[0] == (byte)'-') { neg = true; i = 1; }
        else if (v[0] == (byte)'+') i = 1;
        if (i >= v.Length) return false;

        // Strip underscores into a small buffer.
        Span<byte> buf = v.Length <= 128 ? stackalloc byte[v.Length] : new byte[v.Length];
        int n = 0;
        for (int k = i; k < v.Length; k++)
        {
            if (v[k] != (byte)'_') buf[n++] = v[k];
        }
        ReadOnlySpan<byte> s = buf.Slice(0, n);
        if (s.Length == 0 || v[i] == (byte)'_') return false;

        UInt128 acc = 0;
        int radix = 10;
        int start = 0;
        if (s.Length > 2 && s[0] == (byte)'0' && s[1] == (byte)'x') { radix = 16; start = 2; }
        else if (s.Length > 2 && s[0] == (byte)'0' && s[1] == (byte)'b') { radix = 2; start = 2; }
        else if (s.Length > 1 && s[0] == (byte)'0') { radix = 8; start = 1; }

        if (radix == 10 && s.IndexOf((byte)':') >= 0)
        {
            // Sexagesimal: [1-9][0-9]*(:[0-5]?[0-9])+
            long total = 0;
            int pos = 0;
            bool first = true;
            while (pos <= s.Length)
            {
                int colon = s.Slice(pos).IndexOf((byte)':');
                ReadOnlySpan<byte> part = colon < 0 ? s.Slice(pos) : s.Slice(pos, colon);
                if (part.Length == 0 || part.Length > 18 && first) return false;
                long seg = 0;
                foreach (byte b in part)
                {
                    if ((uint)(b - (byte)'0') > 9) return false;
                    seg = seg * 10 + (b - '0');
                }
                if (!first && (part.Length > 2 || seg > 59)) return false;
                total = total * 60 + seg;
                first = false;
                if (colon < 0) break;
                pos += colon + 1;
            }
            if (first) return false;
            value = neg ? -total : total;
            return true;
        }

        for (int k = start; k < s.Length; k++)
        {
            byte b = s[k];
            int d;
            if (b >= (byte)'0' && b <= (byte)'9') d = b - '0';
            else if (radix == 16 && (b | 0x20) >= (byte)'a' && (b | 0x20) <= (byte)'f') d = (b | 0x20) - 'a' + 10;
            else return false;
            if (d >= radix) return false;
            acc = acc * (uint)radix + (uint)d;
            if (acc > (UInt128)long.MaxValue + 1)
            {
                overflow = true;
                return false;
            }
        }
        if (start == s.Length) return false;
        if (neg)
        {
            value = (long)(0 - (long)(ulong)acc);
            if (acc == (UInt128)long.MaxValue + 1) value = long.MinValue;
            return true;
        }
        if (acc > long.MaxValue) { overflow = true; return false; }
        value = (long)acc;
        return true;
    }

    private static bool TryParseDoubleYaml11(ReadOnlySpan<byte> v, out double value)
    {
        value = 0;
        if (v.Length == 0) return false;
        Span<byte> buf = v.Length <= 128 ? stackalloc byte[v.Length] : new byte[v.Length];
        int n = 0;
        foreach (byte b in v)
        {
            if (b != (byte)'_') buf[n++] = b;
        }
        ReadOnlySpan<byte> s = buf.Slice(0, n);
        if (s.IndexOf((byte)':') >= 0)
        {
            bool neg = s[0] == (byte)'-';
            ReadOnlySpan<byte> body = (s[0] == (byte)'-' || s[0] == (byte)'+') ? s.Slice(1) : s;
            double total = 0;
            int pos = 0;
            bool first = true;
            while (true)
            {
                int colon = body.Slice(pos).IndexOf((byte)':');
                ReadOnlySpan<byte> part = colon < 0 ? body.Slice(pos) : body.Slice(pos, colon);
                if (part.Length == 0) return false;
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double seg)) return false;
                if (!first && (seg < 0 || seg >= 60)) return false;
                total = total * 60 + seg;
                first = false;
                if (colon < 0) break;
                pos += colon + 1;
            }
            value = neg ? -total : total;
            return true;
        }
        if (ResolveCoreNumber(s) == YamlScalarKind.String) return false;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
