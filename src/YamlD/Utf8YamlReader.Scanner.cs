using System.Buffers;
using System.Runtime.CompilerServices;

namespace YamlD;

public ref partial struct Utf8YamlReader
{
    // ---------------------------------------------------------------------------------------------
    // Character helpers
    // ---------------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly byte At(int i) => (uint)i < (uint)_data.Length ? _data[i] : (byte)0;

    /// <summary>Space, tab, CR, LF or end-of-input (NUL is never valid YAML data, so it doubles as EOF).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBlankZ(byte b) => b <= 32 && ((0x1_0000_2601UL >> b) & 1) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBlank(byte b) => b == (byte)' ' || b == (byte)'\t';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBreak(byte b) => b == (byte)'\n' || b == (byte)'\r';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBreakZ(byte b) => b == (byte)'\n' || b == (byte)'\r' || b == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsFlowInd(byte b) => b == (byte)',' || (b | 0x20) == 0x7B || (b | 0x20) == 0x7D;

    private static readonly SearchValues<byte> s_dqStops = SearchValues.Create("\"\\\n\r"u8);
    private static readonly SearchValues<byte> s_sqStops = SearchValues.Create("'\n\r"u8);
    private static readonly SearchValues<byte> s_breaks = SearchValues.Create("\n\r"u8);

    private readonly bool IsDocIndicatorAt(int p)
    {
        byte c = At(p);
        if (c != (byte)'-' && c != (byte)'.') return false;
        return At(p + 1) == c && At(p + 2) == c && IsBlankZ(At(p + 3));
    }

    // ---------------------------------------------------------------------------------------------
    // Token queue
    // ---------------------------------------------------------------------------------------------

    private void EnsureTokenSpace()
    {
        Token[] t = _tokens!;
        if (_tokHead + _tokCount < t.Length) return;

        if (_tokHead > 0)
        {
            Array.Copy(t, _tokHead, t, 0, _tokCount);
            _tokHead = 0;
            return;
        }

        Token[] bigger = ArrayPool<Token>.Shared.Rent(t.Length * 2);
        Array.Copy(t, bigger, _tokCount);
        ArrayPool<Token>.Shared.Return(t);
        _tokens = bigger;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Enqueue(TokenKind kind, int start, int end = 0, int a = 0, int b = 0, byte style = 0, TokenFlags flags = 0)
    {
        EnsureTokenSpace();
        ref Token t = ref _tokens![_tokHead + _tokCount];
        t.Kind = kind;
        t.Style = style;
        t.Flags = flags;
        t.Start = start;
        t.End = end;
        t.A = a;
        t.B = b;
        _tokCount++;
    }

    private void InsertToken(int index, TokenKind kind, int start)
    {
        EnsureTokenSpace();
        Token[] t = _tokens!;
        int abs = _tokHead + index;
        for (int j = _tokHead + _tokCount; j > abs; j--)
        {
            t[j] = t[j - 1];
        }
        t[abs] = new Token { Kind = kind, Start = start, End = start };
        _tokCount++;
    }

    // ---------------------------------------------------------------------------------------------
    // Indentation & simple keys
    // ---------------------------------------------------------------------------------------------

    private void RollIndent(int column, long number, TokenKind kind, int start)
    {
        if (_flowLevel > 0) return;
        if (_indent < column)
        {
            if (_indentsCount == _indents!.Length)
            {
                int[] bigger = ArrayPool<int>.Shared.Rent(_indents.Length * 2);
                Array.Copy(_indents, bigger, _indentsCount);
                ArrayPool<int>.Shared.Return(_indents);
                _indents = bigger;
            }
            _indents[_indentsCount++] = _indent;
            _indent = column;
            if (number == -1)
            {
                Enqueue(kind, start, start);
            }
            else
            {
                InsertToken((int)(number - _tokensParsed), kind, start);
            }
        }
    }

    private void UnrollIndent(int column)
    {
        if (_flowLevel > 0) return;
        while (_indent > column)
        {
            Enqueue(TokenKind.BlockEnd, _pos, _pos);
            _indent = _indents![--_indentsCount];
        }
    }

    private void StaleSimpleKeys()
    {
        if (_possibleKeys == 0) return;
        SimpleKey[] keys = _simpleKeys!;
        for (int i = 0; i < _simpleKeysCount; i++)
        {
            ref SimpleKey k = ref keys[i];
            if (k.Possible && !k.Multiline && (k.Line < _line || k.Pos + 1024 < _pos))
            {
                if (k.Required)
                {
                    Throw(k.Pos, "Could not find expected ':' after a mapping key");
                }
                k.Possible = false;
                _possibleKeys--;
            }
        }
    }

    private void SaveSimpleKey()
    {
        if (!_simpleKeyAllowed) return;
        int col = _pos - _lineStart;
        ref SimpleKey k = ref _simpleKeys![_simpleKeysCount - 1];
        if (k.Possible && k.Required)
        {
            Throw(k.Pos, "Could not find expected ':' after a mapping key");
        }
        if (!k.Possible) _possibleKeys++;
        k.Possible = true;
        k.Required = _flowLevel == 0 && _indent == col;
        k.TokenNumber = _tokensParsed + _tokCount;
        k.Pos = _pos;
        k.Line = _line;
        k.Column = col;
        k.TabbedStart = _pos == _tabTokenPos;
    }

    private void RemoveSimpleKey()
    {
        ref SimpleKey k = ref _simpleKeys![_simpleKeysCount - 1];
        if (k.Possible)
        {
            if (k.Required)
            {
                Throw(k.Pos, "Could not find expected ':' after a mapping key");
            }
            k.Possible = false;
            _possibleKeys--;
        }
    }

    /// <summary>
    /// Called right after a scalar-like token ended at <c>_pos</c>: if what follows on the line cannot be the value
    /// indicator, the pending simple key can never become a key, so the token can be released to the parser
    /// without scanning ahead (saves a lookahead round trip per scalar).
    /// </summary>
    private void SettleSimpleKey()
    {
        ref SimpleKey k = ref _simpleKeys![_simpleKeysCount - 1];
        if (!k.Possible || k.Required) return;

        int p = _pos;
        byte c = At(p);
        while (c == (byte)' ' || c == (byte)'\t')
        {
            c = At(++p);
        }
        if (c == (byte)':') return;
        if (k.Multiline && (c == (byte)'\n' || c == (byte)'\r' || c == 0 || c == (byte)'#')) return;

        k.Possible = false;
        _possibleKeys--;
    }

    private void IncreaseFlowLevel(bool mapping)
    {
        if (_flowLevel + 1 > _maxDepth)
        {
            Throw(_pos, "The maximum nesting depth was exceeded");
        }
        if (_simpleKeysCount == _simpleKeys!.Length)
        {
            SimpleKey[] bigger = ArrayPool<SimpleKey>.Shared.Rent(_simpleKeys.Length * 2);
            Array.Copy(_simpleKeys, bigger, _simpleKeysCount);
            ArrayPool<SimpleKey>.Shared.Return(_simpleKeys);
            _simpleKeys = bigger;
        }
        _simpleKeys[_simpleKeysCount++] = new SimpleKey { Multiline = mapping };
        _flowLevel++;
    }

    private void DecreaseFlowLevel()
    {
        if (_flowLevel > 0)
        {
            _flowLevel--;
            if (_simpleKeys![--_simpleKeysCount].Possible) _possibleKeys--;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Token fetching
    // ---------------------------------------------------------------------------------------------

    private void FetchMoreTokens()
    {
        for (;;)
        {
            bool need = false;
            if (_tokCount == 0)
            {
                need = true;
            }
            else if (_possibleKeys == 0)
            {
                return;
            }
            else
            {
                // Only the outermost live possible key can block the head token: keys of inner flow levels were
                // saved later, hence have larger token numbers. Stale keys are dropped on the way.
                SimpleKey[] keys = _simpleKeys!;
                for (int i = 0; i < _simpleKeysCount; i++)
                {
                    ref SimpleKey k = ref keys[i];
                    if (!k.Possible) continue;
                    if (!k.Multiline && (k.Line < _line || k.Pos + 1024 < _pos))
                    {
                        if (k.Required)
                        {
                            Throw(k.Pos, "Could not find expected ':' after a mapping key");
                        }
                        k.Possible = false;
                        _possibleKeys--;
                        continue;
                    }
                    need = k.TokenNumber == _tokensParsed;
                    break;
                }
            }

            if (!need || _streamEnded) return;
            FetchNextToken();
        }
    }

    private void FetchNextToken()
    {
        bool prevJsonLike = _prevJsonLike;
        _prevJsonLike = false;

        ScanToNextToken();
        StaleSimpleKeys();

        int pos = _pos;
        int col = pos - _lineStart;
        UnrollIndent(col);

        if (pos >= _data.Length)
        {
            FetchStreamEnd();
            return;
        }

        byte c = _data[pos];
        if (YamlTables.TokenStart[c] == 0)
        {
            FetchPlainScalar();
            return;
        }

        byte n;
        switch (c)
        {
            case (byte)'%':
                if (col == 0)
                {
                    FetchDirective();
                    return;
                }
                break;

            case (byte)'-':
                if (col == 0 && At(pos + 1) == (byte)'-' && At(pos + 2) == (byte)'-' && IsBlankZ(At(pos + 3)))
                {
                    FetchDocumentIndicator(TokenKind.DocumentStart);
                    return;
                }
                if (IsBlankZ(At(pos + 1)))
                {
                    FetchBlockEntry();
                    return;
                }
                break;

            case (byte)'.':
                if (col == 0 && At(pos + 1) == (byte)'.' && At(pos + 2) == (byte)'.' && IsBlankZ(At(pos + 3)))
                {
                    FetchDocumentIndicator(TokenKind.DocumentEnd);
                    return;
                }
                break;

            case (byte)'[':
                FetchFlowCollectionStart(TokenKind.FlowSequenceStart);
                return;
            case (byte)'{':
                FetchFlowCollectionStart(TokenKind.FlowMappingStart);
                return;
            case (byte)']':
                FetchFlowCollectionEnd(TokenKind.FlowSequenceEnd);
                return;
            case (byte)'}':
                FetchFlowCollectionEnd(TokenKind.FlowMappingEnd);
                return;
            case (byte)',':
                FetchFlowEntry();
                return;

            case (byte)'?':
                n = At(pos + 1);
                if (IsBlankZ(n) || (_flowLevel > 0 && IsFlowInd(n)))
                {
                    FetchKey();
                    return;
                }
                break;

            case (byte)':':
                n = At(pos + 1);
                if (IsBlankZ(n) || (_flowLevel > 0 && (IsFlowInd(n) || prevJsonLike)))
                {
                    FetchValue();
                    return;
                }
                break;

            case (byte)'*':
                FetchAnchor(TokenKind.Alias);
                return;
            case (byte)'&':
                FetchAnchor(TokenKind.Anchor);
                return;
            case (byte)'!':
                FetchTag();
                return;

            case (byte)'|':
                if (_flowLevel == 0)
                {
                    FetchBlockScalar(literal: true);
                    return;
                }
                break;
            case (byte)'>':
                if (_flowLevel == 0)
                {
                    FetchBlockScalar(literal: false);
                    return;
                }
                break;

            case (byte)'\'':
                FetchFlowScalar(single: true);
                return;
            case (byte)'"':
                FetchFlowScalar(single: false);
                return;
        }

        // Plain scalar: not a blank/indicator, or an indicator that is followed by a safe character.
        if (CanStartPlain(c, pos))
        {
            FetchPlainScalar();
            return;
        }

        Throw(pos, "Found a character that cannot start any token");
    }

    private readonly bool CanStartPlain(byte c, int pos)
    {
        switch (c)
        {
            case (byte)'-':
            case (byte)'?':
            case (byte)':':
                // Only reached when not followed by whitespace (those were handled as indicators);
                // in flow context the next character must also not be a flow indicator.
                byte next = At(pos + 1);
                return !IsBlankZ(next) && !(_flowLevel > 0 && IsFlowInd(next));
            case (byte)',':
            case (byte)'[':
            case (byte)']':
            case (byte)'{':
            case (byte)'}':
            case (byte)'#':
            case (byte)'&':
            case (byte)'*':
            case (byte)'!':
            case (byte)'|':
            case (byte)'>':
            case (byte)'\'':
            case (byte)'"':
            case (byte)'%':
            case (byte)'@':
            case (byte)'`':
                return false;
            default:
                return !IsBlankZ(c);
        }
    }

    private void ScanToNextToken()
    {
        ReadOnlySpan<byte> data = _data;
        int pos = _pos;

        // Already on a token start: nothing to skip (the common case right after `key:`'s space or a comma).
        if ((uint)pos < (uint)data.Length)
        {
            byte b0 = data[pos];
            if (b0 > (byte)' ' && b0 != (byte)'#' && b0 != 0xEF) return;
        }

        bool crossedBreak = false;

        for (;;)
        {
            int wsStart = pos;
            bool tab = false;
            for (;; pos++)
            {
                byte b = At(pos);
                if (b == (byte)' ') continue;
                if (b == (byte)'\t') { tab = true; continue; }
                break;
            }

            byte c = At(pos);
            if (c == (byte)'#')
            {
                if (pos != _lineStart && pos > 0 && !IsBlank(data[pos - 1]) && !IsBreak(data[pos - 1]))
                {
                    Throw(pos, "A comment must be separated from other tokens by whitespace");
                }
                int rel = data.Slice(pos).IndexOfAny(s_breaks);
                pos = rel < 0 ? data.Length : pos + rel;
                c = At(pos);
                tab = false;
            }

            if (c == (byte)'\n' || c == (byte)'\r')
            {
                pos += (c == (byte)'\r' && At(pos + 1) == (byte)'\n') ? 2 : 1;
                _line++;
                _lineStart = pos;
                crossedBreak = true;
                if (_flowLevel == 0) _simpleKeyAllowed = true;
                continue;
            }

            if (pos >= data.Length) break;

            if (tab && wsStart == _lineStart && _flowLevel == 0)
            {
                // Tabs may follow the indentation spaces but never replace them.
                int spaces = 0;
                while (At(_lineStart + spaces) == (byte)' ') spaces++;
                if (spaces <= _indent)
                {
                    Throw(wsStart, "A tab character cannot be used for indentation");
                }
                _tabTokenPos = pos;
            }

            if (c == 0xEF && pos == _lineStart && _flowLevel == 0 && _indent == -1
                && At(pos + 1) == 0xBB && At(pos + 2) == 0xBF)
            {
                // Byte order mark between documents.
                pos += 3;
                _lineStart = pos;
                continue;
            }

            if (crossedBreak && _flowLevel > 0)
            {
                int spaces = 0;
                while (At(_lineStart + spaces) == (byte)' ') spaces++;
                if (spaces <= _indent)
                {
                    Throw(pos, "Flow collection content is not indented enough");
                }
            }

            break;
        }

        _pos = pos;
    }

    private void FetchStreamEnd()
    {
        UnrollIndent(-1);
        RemoveSimpleKey();
        _simpleKeyAllowed = false;
        Enqueue(TokenKind.StreamEnd, _pos, _pos);
        _streamEnded = true;
    }

    private void FetchDocumentIndicator(TokenKind kind)
    {
        UnrollIndent(-1);
        RemoveSimpleKey();
        _simpleKeyAllowed = false;
        int start = _pos;
        _pos += 3;

        if (kind == TokenKind.DocumentEnd)
        {
            // Only whitespace and a comment may follow the document end marker.
            int p = _pos;
            bool sawBlank = false;
            while (IsBlank(At(p))) { p++; sawBlank = true; }
            byte c = At(p);
            if (!IsBreakZ(c) && !(c == (byte)'#' && sawBlank))
            {
                Throw(p, "Content is not allowed after the document end marker");
            }
        }

        Enqueue(kind, start, _pos);
    }

    private void FetchFlowCollectionStart(TokenKind kind)
    {
        SaveSimpleKey();
        IncreaseFlowLevel(kind == TokenKind.FlowMappingStart);
        _simpleKeyAllowed = true;
        Enqueue(kind, _pos, _pos + 1);
        _pos++;
    }

    private void FetchFlowCollectionEnd(TokenKind kind)
    {
        RemoveSimpleKey();
        DecreaseFlowLevel();
        _simpleKeyAllowed = false;
        Enqueue(kind, _pos, _pos + 1);
        _pos++;
        _prevJsonLike = true;
        SettleSimpleKey();
    }

    private void FetchFlowEntry()
    {
        RemoveSimpleKey();
        _simpleKeyAllowed = true;
        Enqueue(TokenKind.FlowEntry, _pos, _pos + 1);
        _pos++;
    }

    private void FetchBlockEntry()
    {
        int col = _pos - _lineStart;
        if (_flowLevel == 0)
        {
            if (!_simpleKeyAllowed)
            {
                Throw(_pos, "Block sequence entries are not allowed in this context");
            }
            ThrowIfTabIndent();
            RollIndent(col, -1, TokenKind.BlockSequenceStart, _pos);
        }
        RemoveSimpleKey();
        _simpleKeyAllowed = true;
        Enqueue(TokenKind.BlockEntry, _pos, _pos + 1);
        _pos++;
        CheckTabAfterIndicator();
    }

    private void FetchKey()
    {
        int col = _pos - _lineStart;
        if (_flowLevel == 0)
        {
            if (!_simpleKeyAllowed)
            {
                Throw(_pos, "Mapping keys are not allowed in this context");
            }
            ThrowIfTabIndent();
            RollIndent(col, -1, TokenKind.BlockMappingStart, _pos);
        }
        RemoveSimpleKey();
        _simpleKeyAllowed = _flowLevel == 0;
        Enqueue(TokenKind.Key, _pos, _pos + 1);
        _pos++;
        CheckTabAfterIndicator();
    }

    /// <summary>
    /// After <c>-</c>, <c>?</c> or <c>:</c> in block context, a tab in the separation is fine before a flow node but must
    /// not precede a block collection (tabs are never indentation): remember where the next token starts.
    /// </summary>
    private void CheckTabAfterIndicator()
    {
        if (_flowLevel != 0) return;
        byte first = At(_pos);
        if (first == (byte)' ')
        {
            byte second = At(_pos + 1);
            if (second != (byte)' ' && second != (byte)'	') return;
        }
        else if (first != (byte)'\t')
        {
            return;
        }
        int p = _pos;
        bool tab = false;
        for (;; p++)
        {
            byte b = At(p);
            if (b == (byte)' ') continue;
            if (b == (byte)'	') { tab = true; continue; }
            break;
        }
        if (tab) _tabTokenPos = p;
    }

    private void ThrowIfTabIndent()
    {
        if (_flowLevel == 0 && _pos == _tabTokenPos)
        {
            Throw(_pos, "A tab character cannot be used for indentation");
        }
    }

    private void FetchValue()
    {
        ref SimpleKey key = ref _simpleKeys![_simpleKeysCount - 1];
        if (key.Possible)
        {
            if (key.TabbedStart && _flowLevel == 0)
            {
                Throw(key.Pos, "A tab character cannot be used for indentation");
            }
            InsertToken((int)(key.TokenNumber - _tokensParsed), TokenKind.Key, key.Pos);
            RollIndent(key.Column, key.TokenNumber, TokenKind.BlockMappingStart, key.Pos);
            key.Possible = false;
            _possibleKeys--;
            _simpleKeyAllowed = false;
        }
        else
        {
            if (_flowLevel == 0)
            {
                if (!_simpleKeyAllowed)
                {
                    Throw(_pos, "Mapping values are not allowed in this context");
                }
                ThrowIfTabIndent();
                RollIndent(_pos - _lineStart, -1, TokenKind.BlockMappingStart, _pos);
            }
            _simpleKeyAllowed = _flowLevel == 0;
        }

        Enqueue(TokenKind.Value, _pos, _pos + 1);
        _pos++;
        CheckTabAfterIndicator();
    }

    private void FetchAnchor(TokenKind kind)
    {
        SaveSimpleKey();
        _simpleKeyAllowed = false;

        int start = _pos + 1;
        int p = start;
        for (;;)
        {
            byte b = At(p);
            if (IsBlankZ(b) || IsFlowInd(b)) break;
            p++;
        }
        if (p == start)
        {
            Throw(_pos, kind == TokenKind.Alias ? "An alias name must not be empty" : "An anchor name must not be empty");
        }

        Enqueue(kind, start, p);
        _pos = p;
    }

    // ---------------------------------------------------------------------------------------------
    // Directives
    // ---------------------------------------------------------------------------------------------

    private void FetchDirective()
    {
        UnrollIndent(-1);
        RemoveSimpleKey();
        _simpleKeyAllowed = false;

        ReadOnlySpan<byte> data = _data;
        int p = _pos + 1;
        int nameStart = p;
        while (true)
        {
            byte b = At(p);
            if ((b >= (byte)'0' && b <= (byte)'9') || (b >= (byte)'a' && b <= (byte)'z') || (b >= (byte)'A' && b <= (byte)'Z') || b == (byte)'-' || b == (byte)'_') p++;
            else break;
        }
        if (p == nameStart)
        {
            Throw(p, "A directive name must not be empty");
        }

        ReadOnlySpan<byte> name = data.Slice(nameStart, p - nameStart);
        int directiveStart = _pos;

        if (name.SequenceEqual("YAML"u8))
        {
            int q = SkipBlanksRequired(p, "Expected whitespace after %YAML");
            int major = ScanVersionNumber(ref q);
            if (At(q) != (byte)'.') Throw(q, "Expected '.' in the %YAML version");
            q++;
            int minor = ScanVersionNumber(ref q);
            p = q;
            FinishDirectiveLine(ref p);
            Enqueue(TokenKind.VersionDirective, directiveStart, p, major, minor);
        }
        else if (name.SequenceEqual("TAG"u8))
        {
            int q = SkipBlanksRequired(p, "Expected whitespace after %TAG");
            int hStart = q;
            if (At(q) != (byte)'!') Throw(q, "A tag handle must start with '!'");
            q++;
            while (IsWordChar(At(q))) q++;
            if (At(q) == (byte)'!')
            {
                q++;
            }
            else if (q - hStart > 1)
            {
                Throw(q, "A tag handle must end with '!'");
            }
            int hEnd = q;
            q = SkipBlanksRequired(q, "Expected whitespace after the tag handle");
            int pStart = q;
            while (IsUriChar(At(q)))
            {
                if (At(q) == (byte)'%') ValidatePercent(q);
                q += At(q) == (byte)'%' ? 3 : 1;
            }
            if (q == pStart) Throw(q, "A tag prefix must not be empty");
            if (!IsBlankZ(At(q))) Throw(q, "Invalid character in the tag prefix");
            p = q;
            int pEnd = q;
            FinishDirectiveLine(ref p);
            Enqueue(TokenKind.TagDirective, hStart, hEnd, pStart, pEnd);
        }
        else
        {
            // Reserved directive: ignored (YAML 1.2.2 §6.8).
            int rel = data.Slice(p).IndexOfAny(s_breaks);
            _pos = rel < 0 ? data.Length : p + rel;
            return;
        }

        _pos = p;
    }

    private int SkipBlanksRequired(int p, string message)
    {
        if (!IsBlank(At(p))) Throw(p, message);
        while (IsBlank(At(p))) p++;
        return p;
    }

    private int ScanVersionNumber(ref int p)
    {
        int start = p;
        int value = 0;
        while (At(p) >= (byte)'0' && At(p) <= (byte)'9')
        {
            value = value * 10 + (At(p) - '0');
            if (p - start > 8) Throw(p, "The %YAML version number is too long");
            p++;
        }
        if (p == start) Throw(p, "Expected a digit in the %YAML version");
        return value;
    }

    private void FinishDirectiveLine(ref int p)
    {
        bool blank = false;
        while (IsBlank(At(p))) { p++; blank = true; }
        byte c = At(p);
        if (c == (byte)'#')
        {
            if (!blank) Throw(p, "A comment must be separated from other tokens by whitespace");
            int rel = _data.Slice(p).IndexOfAny(s_breaks);
            p = rel < 0 ? _data.Length : p + rel;
            return;
        }
        if (!IsBreakZ(c))
        {
            Throw(p, "Expected a comment or a line break after the directive");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Tags
    // ---------------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsWordChar(byte b) =>
        (b >= (byte)'0' && b <= (byte)'9') || (b >= (byte)'a' && b <= (byte)'z') || (b >= (byte)'A' && b <= (byte)'Z') || b == (byte)'-';

    // ns-uri-char (ASCII): word chars, '%', and ;/?:@&=+$,_.!~*'()[] plus '#'.
    private static bool IsUriChar(byte b)
    {
        if (IsWordChar(b)) return true;
        switch (b)
        {
            case (byte)'%': case (byte)'#': case (byte)';': case (byte)'/': case (byte)'?': case (byte)':':
            case (byte)'@': case (byte)'&': case (byte)'=': case (byte)'+': case (byte)'$': case (byte)',':
            case (byte)'_': case (byte)'.': case (byte)'!': case (byte)'~': case (byte)'*': case (byte)'\'':
            case (byte)'(': case (byte)')': case (byte)'[': case (byte)']':
                return true;
            default:
                return false;
        }
    }

    private static bool IsTagChar(byte b) => b != (byte)'!' && !IsFlowInd(b) && IsUriChar(b);

    private void ValidatePercent(int p)
    {
        if (!IsHex(At(p + 1)) || !IsHex(At(p + 2)))
        {
            Throw(p, "Invalid percent-escape in a tag or URI");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsHex(byte b) =>
        (b >= (byte)'0' && b <= (byte)'9') || ((b | 0x20) >= (byte)'a' && (b | 0x20) <= (byte)'f');

    private void FetchTag()
    {
        SaveSimpleKey();
        _simpleKeyAllowed = false;

        int start = _pos;
        int p = start + 1;
        if (At(p) == (byte)'<')
        {
            // Verbatim tag !<...>
            p++;
            int uriStart = p;
            while (true)
            {
                byte b = At(p);
                if (b == (byte)'>' || !IsUriChar(b)) break;
                if (b == (byte)'%') { ValidatePercent(p); p += 3; }
                else p++;
            }
            if (p == uriStart) Throw(p, "A verbatim tag must not be empty");
            if (At(p) != (byte)'>') Throw(p, "A verbatim tag must end with '>'");
            Enqueue(TokenKind.Tag, start, start, uriStart, p, flags: TokenFlags.Verbatim);
            p++;
        }
        else
        {
            int q = p;
            while (IsWordChar(At(q))) q++;
            if (At(q) == (byte)'!')
            {
                // Named (!e!) or secondary (!!) handle.
                int handleEnd = q + 1;
                int sufStart = handleEnd;
                int s = sufStart;
                while (IsTagChar(At(s)))
                {
                    if (At(s) == (byte)'%') { ValidatePercent(s); s += 3; }
                    else s++;
                }
                if (s == sufStart) Throw(s, "A tag shorthand must have a non-empty suffix");
                Enqueue(TokenKind.Tag, start, handleEnd, sufStart, s);
                p = s;
            }
            else
            {
                // Primary handle '!' followed by a local suffix, or the non-specific tag '!'.
                int sufStart = p;
                int s = sufStart;
                while (IsTagChar(At(s)))
                {
                    if (At(s) == (byte)'%') { ValidatePercent(s); s += 3; }
                    else s++;
                }
                if (s == sufStart)
                {
                    Enqueue(TokenKind.Tag, start, start, start, start + 1, flags: TokenFlags.Verbatim);
                }
                else
                {
                    Enqueue(TokenKind.Tag, start, start + 1, sufStart, s);
                }
                p = s;
            }
        }

        byte next = At(p);
        if (!IsBlankZ(next) && !(_flowLevel > 0 && IsFlowInd(next)))
        {
            Throw(p, "Expected whitespace or a line break after a tag");
        }
        _pos = p;
    }
}
