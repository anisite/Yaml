using System.Buffers;
using System.Runtime.CompilerServices;

namespace System.Text.Yaml;

public ref partial struct Utf8YamlReader
{
    // ---------------------------------------------------------------------------------------------
    // Plain scalars
    // ---------------------------------------------------------------------------------------------

    private void FetchPlainScalar()
    {
        SaveSimpleKey();
        _simpleKeyAllowed = false;

        ReadOnlySpan<byte> data = _data;
        int start = _pos;
        int p = start;
        int end = start;
        bool multiline = false;
        bool flow = _flowLevel > 0;
        int blockIndent = _indent + 1; // continuation lines must be indented at least this much (block context)
        int line = _line;
        int lineStart = _lineStart;
        bool crossedBreak = false;

        for (;;)
        {
            // Document markers terminate the scalar.
            if (p == lineStart && IsDocIndicatorAt(p)) break;
            if (At(p) == (byte)'#') break;

            int chunkStart = p;
            byte[] stopTable = flow ? YamlTables.PlainStopFlow : YamlTables.PlainStopBlock;
            while ((uint)p < (uint)data.Length)
            {
                byte c = data[p];
                byte cls = stopTable[c];
                if (cls == 0)
                {
                    p++;
                    continue;
                }
                if (cls == 2)
                {
                    byte n = At(p + 1);
                    if (!(IsBlankZ(n) || (flow && IsFlowInd(n))))
                    {
                        p++;
                        continue;
                    }
                }
                break;
            }
            if (p > chunkStart) end = p;

            byte stop = At(p);
            if (!IsBlank(stop) && !IsBreak(stop)) break; // terminator, flow indicator, EOF, or ':'

            // Consume whitespace and line breaks, looking for a continuation.
            int q = p;
            int breaks = 0;
            int ql = line;
            int qls = lineStart;
            for (;;)
            {
                byte b = At(q);
                if (b == (byte)' ')
                {
                    q++;
                }
                else if (b == (byte)'\t')
                {
                    // Tabs are not allowed as indentation of continuation lines (only an error if the line carries content).
                    if (breaks > 0 && !flow && (q - qls) < blockIndent)
                    {
                        int t = q;
                        while (IsBlank(At(t))) t++;
                        if (!IsBreakZ(At(t)))
                        {
                            Throw(q, "A tab character cannot be used for indentation");
                        }
                    }
                    q++;
                }
                else if (b == (byte)'\n' || b == (byte)'\r')
                {
                    q += (b == (byte)'\r' && At(q + 1) == (byte)'\n') ? 2 : 1;
                    breaks++;
                    ql++;
                    qls = q;
                }
                else
                {
                    break;
                }
            }

            if (breaks == 0)
            {
                // Only blanks followed by more content on the same line (or a terminator).
                p = q;
                continue;
            }

            // The scalar may continue on the next non-empty line.
            p = q;
            line = ql;
            lineStart = qls;
            crossedBreak = true;

            if (p >= data.Length) break;
            int col = p - lineStart;
            if (flow)
            {
                if (col <= _indent && !IsDocIndicatorAt(p) && At(p) != (byte)'#')
                {
                    Throw(p, "Flow collection content is not indented enough");
                }
            }
            else if (col < blockIndent)
            {
                break;
            }

            if (At(p) == (byte)'#') break;
            if (IsDocIndicatorAt(p)) break;
            multiline = true;
        }

        if (crossedBreak)
        {
            _simpleKeyAllowed = true;
        }
        _line = line;
        _lineStart = lineStart;
        _pos = p;

        Enqueue(TokenKind.Scalar, start, end, flags: multiline ? TokenFlags.Multiline : TokenFlags.None,
            style: (byte)YamlScalarStyle.Plain);

        // Fast path: `key:` on the same line resolves the simple key right away, skipping a full dispatch round.
        if (!crossedBreak)
        {
            if ((uint)p < (uint)data.Length && data[p] == (byte)':')
            {
                byte n = At(p + 1);
                if (IsBlankZ(n) || (flow && IsFlowInd(n)))
                {
                    FetchValue();
                }
            }
            else
            {
                SettleSimpleKey();
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Quoted scalars
    // ---------------------------------------------------------------------------------------------

    private void FetchFlowScalar(bool single)
    {
        SaveSimpleKey();
        _simpleKeyAllowed = false;

        ReadOnlySpan<byte> data = _data;
        int open = _pos;
        int contentStart = open + 1;
        int p = contentStart;
        TokenFlags flags = TokenFlags.None;
        int line = _line;
        int lineStart = _lineStart;
        SearchValues<byte> stops = single ? s_sqStops : s_dqStops;

        for (;;)
        {
            int rel = data.Slice(p).IndexOfAny(stops);
            if (rel < 0)
            {
                Throw(open, "Unexpected end of stream while scanning a quoted scalar");
            }
            p += rel;
            byte c = data[p];

            if (c == (byte)'"' || c == (byte)'\'')
            {
                if (single && At(p + 1) == (byte)'\'')
                {
                    flags |= TokenFlags.HasEscapes;
                    p += 2;
                    continue;
                }
                break;
            }

            if (c == (byte)'\\')
            {
                byte e = At(p + 1);
                switch (e)
                {
                    case (byte)'0': case (byte)'a': case (byte)'b': case (byte)'t': case (byte)'\t':
                    case (byte)'n': case (byte)'v': case (byte)'f': case (byte)'r': case (byte)'e':
                    case (byte)' ': case (byte)'"': case (byte)'/': case (byte)'\\': case (byte)'N':
                    case (byte)'_': case (byte)'L': case (byte)'P':
                        flags |= TokenFlags.HasEscapes;
                        p += 2;
                        continue;
                    case (byte)'x':
                        RequireHex(p + 2, 2);
                        flags |= TokenFlags.HasEscapes;
                        p += 4;
                        continue;
                    case (byte)'u':
                        RequireHex(p + 2, 4);
                        flags |= TokenFlags.HasEscapes;
                        p += 6;
                        continue;
                    case (byte)'U':
                        RequireHex(p + 2, 8);
                        flags |= TokenFlags.HasEscapes;
                        p += 10;
                        continue;
                    case (byte)'\n':
                    case (byte)'\r':
                        flags |= TokenFlags.HasEscapes | TokenFlags.Multiline;
                        p += 1 + ((e == (byte)'\r' && At(p + 2) == (byte)'\n') ? 2 : 1);
                        line++;
                        lineStart = p;
                        p = ContinueQuotedLine(p, lineStart, open);
                        continue;
                    default:
                        Throw(p, "Found an unknown escape sequence in a double-quoted scalar");
                        break;
                }
            }

            // A line break.
            flags |= TokenFlags.Multiline;
            p += (c == (byte)'\r' && At(p + 1) == (byte)'\n') ? 2 : 1;
            line++;
            lineStart = p;
            p = ContinueQuotedLine(p, lineStart, open);
        }

        _line = line;
        _lineStart = lineStart;
        _pos = p + 1;
        _prevJsonLike = true;
        Enqueue(TokenKind.Scalar, contentStart, p, flags: flags,
            style: (byte)(single ? YamlScalarStyle.SingleQuoted : YamlScalarStyle.DoubleQuoted));

        if (line == _simpleKeys![_simpleKeysCount - 1].Line)
        {
            byte n = At(_pos);
            if (n == (byte)':' && (IsBlankZ(At(_pos + 1)) || _flowLevel > 0))
            {
                // A quoted scalar is JSON-like: in flow context the value may follow with no separation.
                FetchValue();
                _prevJsonLike = false;
            }
            else
            {
                SettleSimpleKey();
            }
        }
    }

    /// <summary>Validates the start of a continuation line inside a quoted scalar and skips its leading blanks.</summary>
    private int ContinueQuotedLine(int p, int lineStart, int open)
    {
        if (IsDocIndicatorAt(p))
        {
            Throw(p, "A document marker is not allowed inside a quoted scalar");
        }
        int s = p;
        while (At(s) == (byte)' ') s++;
        int spaces = s - p;
        while (IsBlank(At(s))) s++;
        byte c = At(s);
        if (!IsBreak(c) && c != 0 && spaces <= _indent)
        {
            Throw(s, "A continuation line of a quoted scalar is not indented enough");
        }
        return s;
    }

    private void RequireHex(int p, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (!IsHex(At(p + i))) Throw(p + i, "Expected a hexadecimal digit in an escape sequence");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Block scalars
    // ---------------------------------------------------------------------------------------------

    private void FetchBlockScalar(bool literal)
    {
        RemoveSimpleKey();
        _simpleKeyAllowed = true;

        ReadOnlySpan<byte> data = _data;
        int p = _pos + 1;
        TokenFlags flags = TokenFlags.None;
        int increment = 0;
        bool haveChomp = false;

        byte c = At(p);
        if (c == (byte)'+' || c == (byte)'-')
        {
            flags |= c == (byte)'+' ? TokenFlags.ChompKeep : TokenFlags.ChompStrip;
            haveChomp = true;
            p++;
            c = At(p);
            if (c >= (byte)'1' && c <= (byte)'9') { increment = c - '0'; p++; }
            else if (c == (byte)'0') Throw(p, "An indentation indicator must be between 1 and 9");
        }
        else if (c >= (byte)'1' && c <= (byte)'9')
        {
            increment = c - '0';
            p++;
            c = At(p);
            if (c == (byte)'+' || c == (byte)'-')
            {
                flags |= c == (byte)'+' ? TokenFlags.ChompKeep : TokenFlags.ChompStrip;
                haveChomp = true;
                p++;
            }
        }
        else if (c == (byte)'0')
        {
            Throw(p, "An indentation indicator must be between 1 and 9");
        }
        _ = haveChomp;

        // Rest of the header line: blanks, optional comment.
        bool blank = false;
        while (IsBlank(At(p))) { p++; blank = true; }
        if (At(p) == (byte)'#')
        {
            if (!blank) Throw(p, "A comment must be separated from other tokens by whitespace");
            int rel = data.Slice(p).IndexOfAny(s_breaks);
            p = rel < 0 ? data.Length : p + rel;
        }
        c = At(p);
        if (!IsBreakZ(c))
        {
            Throw(p, "Expected a comment or a line break after the block scalar header");
        }

        int line = _line;
        int lineStart = _lineStart;
        if (c != 0)
        {
            p += (c == (byte)'\r' && At(p + 1) == (byte)'\n') ? 2 : 1;
            line++;
            lineStart = p;
        }

        int contentStart = p;
        int parentIndent = _indent;
        int indent;

        if (increment > 0)
        {
            indent = parentIndent + increment;
            if (indent < 0) indent = 0;
        }
        else
        {
            // Auto-detect from the first non-empty line.
            int lp = p;
            int maxBlank = 0;
            int detected = -1;
            for (;;)
            {
                int s = lp;
                while (At(s) == (byte)' ') s++;
                int spaces = s - lp;
                byte b = At(s);
                if (b == 0 && s >= data.Length)
                {
                    if (spaces > maxBlank) maxBlank = spaces;
                    break;
                }
                if (IsBreak(b))
                {
                    if (spaces > maxBlank) maxBlank = spaces;
                    lp = s + ((b == (byte)'\r' && At(s + 1) == (byte)'\n') ? 2 : 1);
                    continue;
                }
                detected = spaces;
                break;
            }

            if (detected < 0)
            {
                indent = maxBlank;
            }
            else
            {
                if (maxBlank > detected && detected >= parentIndent + 1)
                {
                    Throw(p, "A leading empty line of a block scalar contains more spaces than the first content line");
                }
                indent = detected;
            }
            if (indent < parentIndent + 1) indent = parentIndent + 1;
            if (indent < 0) indent = 0;
        }

        // Find the end of the scalar: the first non-empty line indented less than `indent`, or a document marker.
        int pos = p;
        int endPos = data.Length;
        while (pos < data.Length)
        {
            int s = pos;
            while (s - pos < indent && At(s) == (byte)' ') s++;
            int spaces = s - pos;
            byte b = At(s);

            if (spaces < indent)
            {
                if (s >= data.Length) break;       // spaces only, at end of input
                if (b == (byte)'	')
                {
                    Throw(s, "A tab character was found where an indentation space is expected");
                }
                if (!IsBreak(b))
                {
                    endPos = pos;                  // less-indented content: the scalar ends here
                    break;
                }
                // otherwise: an empty line, part of the scalar
            }
            else if (spaces == 0 && IsDocIndicatorAt(pos))
            {
                endPos = pos;
                break;
            }

            // Advance to the next line.
            int rel = data.Slice(s).IndexOfAny(s_breaks);
            if (rel < 0) break;
            s += rel;
            pos = s + ((data[s] == (byte)'\r' && At(s + 1) == (byte)'\n') ? 2 : 1);
            line++;
            lineStart = pos;
        }

        _line = line;
        _lineStart = lineStart;
        _pos = endPos;

        Enqueue(TokenKind.Scalar, contentStart, endPos, a: indent,
            flags: flags | TokenFlags.Multiline,
            style: (byte)(literal ? YamlScalarStyle.Literal : YamlScalarStyle.Folded));
    }

    // ---------------------------------------------------------------------------------------------
    // Decoding
    // ---------------------------------------------------------------------------------------------

    private byte[] EnsureScratch(int capacity)
    {
        byte[]? s = _scratch;
        if (s is null || s.Length < capacity)
        {
            if (s is not null) ArrayPool<byte>.Shared.Return(s);
            s = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, 256));
            _scratch = s;
        }
        return s;
    }

    private void SetScalarValue(in Token t)
    {
        _scalarStyle = (YamlScalarStyle)t.Style;
        TokenFlags f = t.Flags;
        if ((f & (TokenFlags.Multiline | TokenFlags.HasEscapes)) == 0)
        {
            _valueStart = t.Start;
            _valueLength = t.End - t.Start;
            _valueInScratch = false;
            return;
        }

        ReadOnlySpan<byte> raw = _data.Slice(t.Start, t.End - t.Start);
        byte[] buf = EnsureScratch(raw.Length + (raw.Length >> 1) + 8);
        int n;
        switch ((YamlScalarStyle)t.Style)
        {
            case YamlScalarStyle.Plain:
                n = DecodePlain(raw, buf);
                break;
            case YamlScalarStyle.SingleQuoted:
                n = DecodeSingleQuoted(raw, buf);
                break;
            case YamlScalarStyle.DoubleQuoted:
                n = DecodeDoubleQuoted(raw, buf, t.Start);
                break;
            default:
                n = DecodeBlock(raw, buf, t.A, (YamlScalarStyle)t.Style == YamlScalarStyle.Literal, f);
                break;
        }
        _valueInScratch = true;
        _valueLength = n;
    }

    /// <summary>
    /// Skips the line-folding whitespace at <paramref name="i"/> (blanks, one break, empty lines) and writes the
    /// folded result. Returns the index of the first character of the next content.
    /// </summary>
    private static int FoldBreaks(ReadOnlySpan<byte> raw, int i, byte[] buf, ref int n, int protectedLen)
    {
        // Trim trailing blanks already written (but not escaped ones).
        while (n > protectedLen && (buf[n - 1] == (byte)' ' || buf[n - 1] == (byte)'\t')) n--;

        int breaks = 0;
        while (i < raw.Length)
        {
            byte b = raw[i];
            if (b == (byte)' ' || b == (byte)'\t')
            {
                i++;
            }
            else if (b == (byte)'\n')
            {
                breaks++;
                i++;
            }
            else if (b == (byte)'\r')
            {
                breaks++;
                i += (i + 1 < raw.Length && raw[i + 1] == (byte)'\n') ? 2 : 1;
            }
            else
            {
                break;
            }
        }

        if (breaks == 1)
        {
            buf[n++] = (byte)' ';
        }
        else
        {
            for (int k = 1; k < breaks; k++) buf[n++] = (byte)'\n';
        }
        return i;
    }

    private static int DecodePlain(ReadOnlySpan<byte> raw, byte[] buf)
    {
        int n = 0;
        int i = 0;
        while (i < raw.Length)
        {
            byte b = raw[i];
            if (b == (byte)'\n' || b == (byte)'\r')
            {
                i = FoldBreaks(raw, i, buf, ref n, 0);
            }
            else
            {
                buf[n++] = b;
                i++;
            }
        }
        return n;
    }

    private static int DecodeSingleQuoted(ReadOnlySpan<byte> raw, byte[] buf)
    {
        int n = 0;
        int i = 0;
        while (i < raw.Length)
        {
            byte b = raw[i];
            if (b == (byte)'\'')
            {
                buf[n++] = (byte)'\'';
                i += 2; // '' => '
            }
            else if (b == (byte)'\n' || b == (byte)'\r')
            {
                i = FoldBreaks(raw, i, buf, ref n, 0);
            }
            else
            {
                buf[n++] = b;
                i++;
            }
        }
        return n;
    }

    private int DecodeDoubleQuoted(ReadOnlySpan<byte> raw, byte[] buf, int baseOffset)
    {
        int n = 0;
        int i = 0;
        int protectedLen = 0;
        while (i < raw.Length)
        {
            byte b = raw[i];
            if (b == (byte)'\\')
            {
                byte e = raw[i + 1];
                i += 2;
                switch (e)
                {
                    case (byte)'0': buf[n++] = 0; break;
                    case (byte)'a': buf[n++] = 7; break;
                    case (byte)'b': buf[n++] = 8; break;
                    case (byte)'t': case (byte)'\t': buf[n++] = 9; break;
                    case (byte)'n': buf[n++] = 10; break;
                    case (byte)'v': buf[n++] = 11; break;
                    case (byte)'f': buf[n++] = 12; break;
                    case (byte)'r': buf[n++] = 13; break;
                    case (byte)'e': buf[n++] = 27; break;
                    case (byte)' ': buf[n++] = 32; break;
                    case (byte)'"': buf[n++] = (byte)'"'; break;
                    case (byte)'/': buf[n++] = (byte)'/'; break;
                    case (byte)'\\': buf[n++] = (byte)'\\'; break;
                    case (byte)'N': n = WriteCodePoint(buf, n, 0x85); break;
                    case (byte)'_': n = WriteCodePoint(buf, n, 0xA0); break;
                    case (byte)'L': n = WriteCodePoint(buf, n, 0x2028); break;
                    case (byte)'P': n = WriteCodePoint(buf, n, 0x2029); break;
                    case (byte)'x':
                        n = WriteCodePoint(buf, n, ParseHex(raw, i, 2));
                        i += 2;
                        break;
                    case (byte)'u':
                    {
                        int cp = ParseHex(raw, i, 4);
                        i += 4;
                        if (cp >= 0xD800 && cp <= 0xDBFF && i + 5 < raw.Length && raw[i] == (byte)'\\' && raw[i + 1] == (byte)'u')
                        {
                            int lo = ParseHex(raw, i + 2, 4);
                            if (lo >= 0xDC00 && lo <= 0xDFFF)
                            {
                                cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                                i += 6;
                            }
                        }
                        if (cp >= 0xD800 && cp <= 0xDFFF)
                        {
                            Throw(baseOffset + i - 6, "An escape sequence denotes an invalid (surrogate) code point");
                        }
                        n = WriteCodePoint(buf, n, cp);
                        break;
                    }
                    case (byte)'U':
                    {
                        int cp = ParseHex(raw, i, 8);
                        i += 8;
                        if (cp < 0 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
                        {
                            Throw(baseOffset + i - 10, "An escape sequence denotes an invalid code point");
                        }
                        n = WriteCodePoint(buf, n, cp);
                        break;
                    }
                    case (byte)'\n':
                    case (byte)'\r':
                    {
                        // Escaped line break: no space is inserted; leading blanks and empty lines follow.
                        if (e == (byte)'\r' && i < raw.Length && raw[i] == (byte)'\n') i++;
                        int breaks = 0;
                        while (i < raw.Length)
                        {
                            byte c = raw[i];
                            if (c == (byte)' ' || c == (byte)'\t') i++;
                            else if (c == (byte)'\n') { breaks++; i++; }
                            else if (c == (byte)'\r') { breaks++; i += (i + 1 < raw.Length && raw[i + 1] == (byte)'\n') ? 2 : 1; }
                            else break;
                        }
                        for (int k = 0; k < breaks; k++) buf[n++] = (byte)'\n';
                        break;
                    }
                }
                protectedLen = n;
            }
            else if (b == (byte)'\n' || b == (byte)'\r')
            {
                i = FoldBreaks(raw, i, buf, ref n, protectedLen);
            }
            else
            {
                buf[n++] = b;
                i++;
            }
        }
        return n;
    }

    private static int ParseHex(ReadOnlySpan<byte> raw, int i, int count)
    {
        int v = 0;
        for (int k = 0; k < count; k++) v = (v << 4) | HexVal(raw[i + k]);
        return v;
    }

    private static int WriteCodePoint(byte[] buf, int n, int cp)
    {
        if (cp < 0x80)
        {
            buf[n++] = (byte)cp;
        }
        else if (cp < 0x800)
        {
            buf[n++] = (byte)(0xC0 | (cp >> 6));
            buf[n++] = (byte)(0x80 | (cp & 0x3F));
        }
        else if (cp < 0x10000)
        {
            buf[n++] = (byte)(0xE0 | (cp >> 12));
            buf[n++] = (byte)(0x80 | ((cp >> 6) & 0x3F));
            buf[n++] = (byte)(0x80 | (cp & 0x3F));
        }
        else
        {
            buf[n++] = (byte)(0xF0 | (cp >> 18));
            buf[n++] = (byte)(0x80 | ((cp >> 12) & 0x3F));
            buf[n++] = (byte)(0x80 | ((cp >> 6) & 0x3F));
            buf[n++] = (byte)(0x80 | (cp & 0x3F));
        }
        return n;
    }

    /// <summary>Decodes the lines of a literal/folded block scalar (YAML 1.2.2 §8.1).</summary>
    private static int DecodeBlock(ReadOnlySpan<byte> raw, byte[] buf, int indent, bool literal, TokenFlags flags)
    {
        int n = 0;
        int i = 0;
        bool pendingBreak = false;      // the break that ended the previous content line
        int trailingBreaks = 0;         // empty lines seen since the last content line
        bool prevStartsBlank = false;   // previous content line started with a blank
        bool first = true;

        while (i < raw.Length)
        {
            // Strip up to `indent` spaces.
            int s = i;
            int stripped = 0;
            while (stripped < indent && s < raw.Length && raw[s] == (byte)' ') { s++; stripped++; }

            // Find the end of the line.
            int e = s;
            while (e < raw.Length && raw[e] != (byte)'\n' && raw[e] != (byte)'\r') e++;
            int next = e;
            bool hasBreak = false;
            if (e < raw.Length)
            {
                hasBreak = true;
                next = e + ((raw[e] == (byte)'\r' && e + 1 < raw.Length && raw[e + 1] == (byte)'\n') ? 2 : 1);
            }

            if (e == s)
            {
                // Empty line (including spaces-only lines up to the indentation). A whitespace-only last line
                // without a terminator still counts as a line (the stream end acts as its line break), which
                // is what the official test suite expects for `- |+` followed by spaces at EOF.
                trailingBreaks++;
                i = next;
                continue;
            }

            bool startsBlank = raw[s] == (byte)' ' || raw[s] == (byte)'\t';

            // Join with what came before.
            if (!first)
            {
                if (!literal && pendingBreak && !prevStartsBlank && !startsBlank)
                {
                    if (trailingBreaks == 0) buf[n++] = (byte)' ';
                    else for (int k = 0; k < trailingBreaks; k++) buf[n++] = (byte)'\n';
                }
                else
                {
                    if (pendingBreak) buf[n++] = (byte)'\n';
                    for (int k = 0; k < trailingBreaks; k++) buf[n++] = (byte)'\n';
                }
            }
            else
            {
                // Leading empty lines are preserved.
                for (int k = 0; k < trailingBreaks; k++) buf[n++] = (byte)'\n';
            }
            first = false;
            trailingBreaks = 0;
            prevStartsBlank = startsBlank;

            raw.Slice(s, e - s).CopyTo(buf.AsSpan(n));
            n += e - s;
            // Same rule for a whitespace-only content line at the very end of the stream.
            pendingBreak = hasBreak || (raw.Slice(s, e - s).IndexOfAnyExcept((byte)' ', (byte)'\t') < 0);
            i = next;
        }

        // Chomping: strip removes every trailing break, clip keeps one, keep preserves all of them.
        if ((flags & TokenFlags.ChompStrip) == 0)
        {
            if (pendingBreak) buf[n++] = (byte)'\n';
            if ((flags & TokenFlags.ChompKeep) != 0)
            {
                for (int k = 0; k < trailingBreaks; k++) buf[n++] = (byte)'\n';
            }
        }
        return n;
    }
}
