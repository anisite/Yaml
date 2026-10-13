using System.Buffers;
using System.Runtime.CompilerServices;

namespace YamlD;

public ref partial struct Utf8YamlReader
{
    /// <summary>
    /// Advances to the next event. Returns <c>false</c> when the end of the stream has been reached.
    /// </summary>
    /// <exception cref="YamlException">The input is not well-formed YAML.</exception>
    public bool Read()
    {
        ResetEvent();
        switch (_state)
        {
            case ParserState.ImplicitDocumentStart:
            case ParserState.DocumentStart:
                return ParseDocumentStart();
            case ParserState.DocumentContent:
                return ParseDocumentContent();
            case ParserState.DocumentEnd:
                return ParseDocumentEnd();
            case ParserState.BlockNode:
                return ParseNode(block: true, indentless: false);
            case ParserState.BlockNodeOrIndentlessSequence:
                return ParseNode(block: true, indentless: true);
            case ParserState.FlowNode:
                return ParseNode(block: false, indentless: false);
            case ParserState.BlockSequenceFirstEntry:
                return ParseBlockSequenceEntry(first: true);
            case ParserState.BlockSequenceEntry:
                return ParseBlockSequenceEntry(first: false);
            case ParserState.IndentlessSequenceEntry:
                return ParseIndentlessSequenceEntry();
            case ParserState.BlockMappingFirstKey:
                return ParseBlockMappingKey(first: true);
            case ParserState.BlockMappingKey:
                return ParseBlockMappingKey(first: false);
            case ParserState.BlockMappingValue:
                return ParseBlockMappingValue();
            case ParserState.FlowSequenceFirstEntry:
                return ParseFlowSequenceEntry(first: true);
            case ParserState.FlowSequenceEntry:
                return ParseFlowSequenceEntry(first: false);
            case ParserState.FlowSequenceEntryMappingKey:
                return ParseFlowSequenceEntryMappingKey();
            case ParserState.FlowSequenceEntryMappingValue:
                return ParseFlowSequenceEntryMappingValue();
            case ParserState.FlowSequenceEntryMappingEnd:
                _state = ParserState.FlowSequenceEntry;
                return EmitEnd(YamlTokenType.EndMapping, _pos);
            case ParserState.FlowMappingFirstKey:
                return ParseFlowMappingKey(first: true);
            case ParserState.FlowMappingKey:
                return ParseFlowMappingKey(first: false);
            case ParserState.FlowMappingValue:
                return ParseFlowMappingValue(empty: false);
            case ParserState.FlowMappingEmptyValue:
                return ParseFlowMappingValue(empty: true);
            default:
                _tokenType = YamlTokenType.None;
                return false;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Token access
    // ---------------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref Token PeekToken()
    {
        if (!_tokenReady)
        {
            FetchMoreTokens();
            _tokenReady = true;
        }
        return ref _tokens![_tokHead];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SkipToken()
    {
        _tokenReady = false;
        _tokensParsed++;
        if (--_tokCount == 0) _tokHead = 0;
        else _tokHead++;
    }

    private void PushState(ParserState s)
    {
        if (_statesCount == _states!.Length)
        {
            ParserState[] bigger = ArrayPool<ParserState>.Shared.Rent(_states.Length * 2);
            Array.Copy(_states, bigger, _statesCount);
            ArrayPool<ParserState>.Shared.Return(_states);
            _states = bigger;
        }
        _states[_statesCount++] = s;
    }

    private void PopState() => _state = _states![--_statesCount];

    // ---------------------------------------------------------------------------------------------
    // Event emission
    // ---------------------------------------------------------------------------------------------

    private void ResetEvent()
    {
        _anchorStart = -1;
        _anchorEnd = -1;
        _tagKind = YamlTagKind.None;
        _explicit = false;
        _valueStart = 0;
        _valueLength = 0;
        _valueInScratch = false;
        _scalarStyle = YamlScalarStyle.Plain;
        _collectionStyle = YamlCollectionStyle.Block;
    }

    private bool EmitStart(YamlTokenType type, YamlCollectionStyle style, int start)
    {
        if (_open >= _maxDepth)
        {
            Throw(start, "The maximum nesting depth was exceeded");
        }
        _tokenType = type;
        _collectionStyle = style;
        _eventStart = start;
        _depth = _open;
        _open++;
        return true;
    }

    private bool EmitEnd(YamlTokenType type, int pos)
    {
        _tokenType = type;
        _eventStart = pos;
        _open--;
        _depth = _open;
        return true;
    }

    private bool EmitEmptyScalar(int pos)
    {
        _tokenType = YamlTokenType.Scalar;
        _eventStart = pos;
        _depth = _open;
        _scalarStyle = YamlScalarStyle.Plain;
        _valueStart = 0;
        _valueLength = 0;
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // Documents
    // ---------------------------------------------------------------------------------------------

    private bool ParseDocumentStart()
    {
        ref Token tok = ref PeekToken();
        bool implicitAllowed = _state == ParserState.ImplicitDocumentStart;

        while (tok.Kind == TokenKind.DocumentEnd)
        {
            SkipToken();
            implicitAllowed = true; // a bare document may follow an explicit end marker
            tok = ref PeekToken();
        }

        TokenKind k = tok.Kind;
        if (implicitAllowed && k != TokenKind.VersionDirective && k != TokenKind.TagDirective
            && k != TokenKind.DocumentStart && k != TokenKind.StreamEnd)
        {
            _versionMajor = 0;
            _versionMinor = 0;
            _tagDirectiveCount = 0;
            PushState(ParserState.DocumentEnd);
            _state = ParserState.BlockNode;
            _tokenType = YamlTokenType.StartDocument;
            _eventStart = tok.Start;
            _explicit = false;
            _depth = _open;
            return true;
        }

        if (k != TokenKind.StreamEnd)
        {
            if (!implicitAllowed && (k == TokenKind.VersionDirective || k == TokenKind.TagDirective))
            {
                Throw(tok.Start, "A directive requires a preceding document end marker ('...')");
            }
            int start = tok.Start;
            ProcessDirectives();
            tok = ref PeekToken();
            if (tok.Kind != TokenKind.DocumentStart)
            {
                Throw(tok.Start, "Did not find the expected document start marker ('---')");
            }
            start = tok.Start;
            SkipToken();
            PushState(ParserState.DocumentEnd);
            _state = ParserState.DocumentContent;
            _tokenType = YamlTokenType.StartDocument;
            _eventStart = start;
            _explicit = true;
            _depth = _open;
            return true;
        }

        _state = ParserState.End;
        _tokenType = YamlTokenType.None;
        return false;
    }

    private void ProcessDirectives()
    {
        _versionMajor = 0;
        _versionMinor = 0;
        _tagDirectiveCount = 0;
        _versionSeen = false;

        for (;;)
        {
            ref Token tok = ref PeekToken();
            if (tok.Kind == TokenKind.VersionDirective)
            {
                if (_versionSeen)
                {
                    Throw(tok.Start, "Found a duplicate %YAML directive");
                }
                if (tok.A != 1)
                {
                    Throw(tok.Start, "Found an incompatible YAML version (only 1.x is supported)");
                }
                _versionSeen = true;
                _versionMajor = tok.A;
                _versionMinor = tok.B;
                SkipToken();
            }
            else if (tok.Kind == TokenKind.TagDirective)
            {
                ReadOnlySpan<byte> handle = _data.Slice(tok.Start, tok.End - tok.Start);
                for (int i = 0; i < _tagDirectiveCount; i++)
                {
                    int hs = _tagDirectives![i * 4], he = _tagDirectives[i * 4 + 1];
                    if (_data.Slice(hs, he - hs).SequenceEqual(handle))
                    {
                        Throw(tok.Start, "Found a duplicate %TAG directive");
                    }
                }
                if ((_tagDirectiveCount + 1) * 4 > (_tagDirectives?.Length ?? 0))
                {
                    int[] bigger = ArrayPool<int>.Shared.Rent(Math.Max(16, (_tagDirectiveCount + 1) * 8));
                    if (_tagDirectives is not null)
                    {
                        Array.Copy(_tagDirectives, bigger, _tagDirectiveCount * 4);
                        ArrayPool<int>.Shared.Return(_tagDirectives);
                    }
                    _tagDirectives = bigger;
                }
                int o = _tagDirectiveCount * 4;
                _tagDirectives![o] = tok.Start;
                _tagDirectives[o + 1] = tok.End;
                _tagDirectives[o + 2] = tok.A;
                _tagDirectives[o + 3] = tok.B;
                _tagDirectiveCount++;
                SkipToken();
            }
            else
            {
                break;
            }
        }
    }

    private bool ParseDocumentContent()
    {
        ref Token tok = ref PeekToken();
        switch (tok.Kind)
        {
            case TokenKind.VersionDirective:
            case TokenKind.TagDirective:
            case TokenKind.DocumentStart:
            case TokenKind.DocumentEnd:
            case TokenKind.StreamEnd:
                PopState();
                return EmitEmptyScalar(tok.Start);
            default:
                return ParseNode(block: true, indentless: false);
        }
    }

    private bool ParseDocumentEnd()
    {
        ref Token tok = ref PeekToken();
        int start = tok.Start;
        bool explicitEnd = false;
        if (tok.Kind == TokenKind.DocumentEnd)
        {
            SkipToken();
            explicitEnd = true;
        }
        _state = explicitEnd ? ParserState.ImplicitDocumentStart : ParserState.DocumentStart;
        _tagDirectiveCount = 0;
        _tokenType = YamlTokenType.EndDocument;
        _eventStart = start;
        _explicit = explicitEnd;
        _depth = _open;
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // Nodes
    // ---------------------------------------------------------------------------------------------

    private bool ParseNode(bool block, bool indentless)
    {
        ref Token tok = ref PeekToken();

        if (tok.Kind == TokenKind.Alias)
        {
            _tokenType = YamlTokenType.Alias;
            _eventStart = tok.Start - 1;
            _depth = _open;
            _valueStart = tok.Start;
            _valueLength = tok.End - tok.Start;
            SkipToken();
            PopState();
            return true;
        }

        int startPos = tok.Start;
        bool hasTag = false;
        Token tagTok = default;

        if (tok.Kind == TokenKind.Anchor)
        {
            _anchorStart = tok.Start;
            _anchorEnd = tok.End;
            SkipToken();
            tok = ref PeekToken();
            if (tok.Kind == TokenKind.Tag)
            {
                tagTok = tok;
                hasTag = true;
                SkipToken();
                tok = ref PeekToken();
            }
        }
        else if (tok.Kind == TokenKind.Tag)
        {
            tagTok = tok;
            hasTag = true;
            SkipToken();
            tok = ref PeekToken();
            if (tok.Kind == TokenKind.Anchor)
            {
                _anchorStart = tok.Start;
                _anchorEnd = tok.End;
                SkipToken();
                tok = ref PeekToken();
            }
        }

        if (hasTag) ResolveTag(in tagTok);

        _eventStart = startPos;
        _depth = _open;

        switch (tok.Kind)
        {
            case TokenKind.BlockEntry when indentless:
                _state = ParserState.IndentlessSequenceEntry;
                return EmitStart(YamlTokenType.StartSequence, YamlCollectionStyle.Block, startPos);

            case TokenKind.Scalar:
                {
                    _tokenType = YamlTokenType.Scalar;
                    SetScalarValue(in tok);
                    SkipToken();
                    PopState();
                    return true;
                }

            case TokenKind.FlowSequenceStart:
                _state = ParserState.FlowSequenceFirstEntry;
                return EmitStart(YamlTokenType.StartSequence, YamlCollectionStyle.Flow, startPos);

            case TokenKind.FlowMappingStart:
                _state = ParserState.FlowMappingFirstKey;
                return EmitStart(YamlTokenType.StartMapping, YamlCollectionStyle.Flow, startPos);

            case TokenKind.BlockSequenceStart when block:
                _state = ParserState.BlockSequenceFirstEntry;
                return EmitStart(YamlTokenType.StartSequence, YamlCollectionStyle.Block, startPos);

            case TokenKind.BlockMappingStart when block:
                _state = ParserState.BlockMappingFirstKey;
                return EmitStart(YamlTokenType.StartMapping, YamlCollectionStyle.Block, startPos);

            default:
                if (_anchorStart >= 0 || hasTag)
                {
                    PopState();
                    _tokenType = YamlTokenType.Scalar;
                    _scalarStyle = YamlScalarStyle.Plain;
                    _valueStart = 0;
                    _valueLength = 0;
                    return true;
                }
                Throw(tok.Start, "Did not find the expected node content");
                return false;
        }
    }

    private void ResolveTag(in Token t)
    {
        ReadOnlySpan<byte> data = _data;
        _tagSuffixStart = t.A;
        _tagSuffixEnd = t.B;

        if ((t.Flags & TokenFlags.Verbatim) != 0)
        {
            _tagPrefixKind = 0;
            _tagSuffixVerbatim = true;
            ReadOnlySpan<byte> full = data.Slice(t.A, t.B - t.A);
            if (full.Length == 1 && full[0] == (byte)'!')
            {
                _tagKind = YamlTagKind.NonSpecific;
                return;
            }
            _tagKind = full.StartsWith("tag:yaml.org,2002:"u8)
                ? ClassifyYamlTag(full.Slice(18))
                : YamlTagKind.Other;
            return;
        }

        _tagSuffixVerbatim = false;
        ReadOnlySpan<byte> handle = data.Slice(t.Start, t.End - t.Start);
        bool found = false;
        for (int i = 0; i < _tagDirectiveCount; i++)
        {
            int hs = _tagDirectives![i * 4], he = _tagDirectives[i * 4 + 1];
            if (data.Slice(hs, he - hs).SequenceEqual(handle))
            {
                _tagPrefixKind = 1;
                _tagPrefixStart = _tagDirectives[i * 4 + 2];
                _tagPrefixEnd = _tagDirectives[i * 4 + 3];
                found = true;
                break;
            }
        }
        if (!found)
        {
            if (handle.Length == 1)
            {
                _tagPrefixKind = 2;
            }
            else if (handle.Length == 2 && handle[1] == (byte)'!')
            {
                _tagPrefixKind = 3;
            }
            else
            {
                Throw(t.Start, "Found an undefined tag handle");
            }
        }

        ReadOnlySpan<byte> suffix = data.Slice(t.A, t.B - t.A);
        bool yamlOrg = _tagPrefixKind == 3
            || (_tagPrefixKind == 1 && data.Slice(_tagPrefixStart, _tagPrefixEnd - _tagPrefixStart).SequenceEqual("tag:yaml.org,2002:"u8));
        _tagKind = yamlOrg ? ClassifyYamlTag(suffix) : YamlTagKind.Other;
    }

    private static YamlTagKind ClassifyYamlTag(ReadOnlySpan<byte> name)
    {
        return name.Length switch
        {
            3 when name.SequenceEqual("str"u8) => YamlTagKind.Str,
            3 when name.SequenceEqual("int"u8) => YamlTagKind.Int,
            3 when name.SequenceEqual("seq"u8) => YamlTagKind.Seq,
            3 when name.SequenceEqual("map"u8) => YamlTagKind.Map,
            4 when name.SequenceEqual("bool"u8) => YamlTagKind.Bool,
            4 when name.SequenceEqual("null"u8) => YamlTagKind.Null,
            5 when name.SequenceEqual("float"u8) => YamlTagKind.Float,
            5 when name.SequenceEqual("merge"u8) => YamlTagKind.Merge,
            _ => YamlTagKind.Other,
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Block collections
    // ---------------------------------------------------------------------------------------------

    private bool ParseBlockSequenceEntry(bool first)
    {
        if (first)
        {
            PeekToken();
            SkipToken(); // BLOCK-SEQUENCE-START
        }

        ref Token tok = ref PeekToken();
        if (tok.Kind == TokenKind.BlockEntry)
        {
            int start = tok.Start;
            SkipToken();
            tok = ref PeekToken();
            if (tok.Kind != TokenKind.BlockEntry && tok.Kind != TokenKind.BlockEnd)
            {
                PushState(ParserState.BlockSequenceEntry);
                return ParseNode(block: true, indentless: false);
            }
            _state = ParserState.BlockSequenceEntry;
            return EmitEmptyScalar(start);
        }

        if (tok.Kind == TokenKind.BlockEnd)
        {
            int pos = tok.Start;
            PopState();
            SkipToken();
            return EmitEnd(YamlTokenType.EndSequence, pos);
        }

        Throw(tok.Start, "Did not find the expected '-' indicator");
        return false;
    }

    private bool ParseIndentlessSequenceEntry()
    {
        ref Token tok = ref PeekToken();
        if (tok.Kind == TokenKind.BlockEntry)
        {
            int start = tok.Start;
            SkipToken();
            tok = ref PeekToken();
            if (tok.Kind != TokenKind.BlockEntry && tok.Kind != TokenKind.Key
                && tok.Kind != TokenKind.Value && tok.Kind != TokenKind.BlockEnd)
            {
                PushState(ParserState.IndentlessSequenceEntry);
                return ParseNode(block: true, indentless: false);
            }
            _state = ParserState.IndentlessSequenceEntry;
            return EmitEmptyScalar(start);
        }

        int pos = tok.Start;
        PopState();
        return EmitEnd(YamlTokenType.EndSequence, pos);
    }

    private bool ParseBlockMappingKey(bool first)
    {
        if (first)
        {
            PeekToken();
            SkipToken(); // BLOCK-MAPPING-START
        }

        ref Token tok = ref PeekToken();
        if (tok.Kind == TokenKind.Key)
        {
            int start = tok.Start;
            SkipToken();
            tok = ref PeekToken();
            if (tok.Kind != TokenKind.Key && tok.Kind != TokenKind.Value && tok.Kind != TokenKind.BlockEnd)
            {
                PushState(ParserState.BlockMappingValue);
                return ParseNode(block: true, indentless: true);
            }
            _state = ParserState.BlockMappingValue;
            return EmitEmptyScalar(start);
        }

        if (tok.Kind == TokenKind.Value)
        {
            // Empty key.
            _state = ParserState.BlockMappingValue;
            return EmitEmptyScalar(tok.Start);
        }

        if (tok.Kind == TokenKind.BlockEnd)
        {
            int pos = tok.Start;
            PopState();
            SkipToken();
            return EmitEnd(YamlTokenType.EndMapping, pos);
        }

        Throw(tok.Start, "Did not find the expected mapping key");
        return false;
    }

    private bool ParseBlockMappingValue()
    {
        ref Token tok = ref PeekToken();
        if (tok.Kind == TokenKind.Value)
        {
            int start = tok.Start;
            SkipToken();
            tok = ref PeekToken();
            if (tok.Kind != TokenKind.Key && tok.Kind != TokenKind.Value && tok.Kind != TokenKind.BlockEnd)
            {
                PushState(ParserState.BlockMappingKey);
                return ParseNode(block: true, indentless: true);
            }
            _state = ParserState.BlockMappingKey;
            return EmitEmptyScalar(start);
        }

        _state = ParserState.BlockMappingKey;
        return EmitEmptyScalar(tok.Start);
    }

    // ---------------------------------------------------------------------------------------------
    // Flow collections
    // ---------------------------------------------------------------------------------------------

    private bool ParseFlowSequenceEntry(bool first)
    {
        if (first)
        {
            PeekToken();
            SkipToken(); // FLOW-SEQUENCE-START
        }

        ref Token tok = ref PeekToken();
        if (tok.Kind != TokenKind.FlowSequenceEnd)
        {
            if (!first)
            {
                if (tok.Kind == TokenKind.FlowEntry)
                {
                    SkipToken();
                    tok = ref PeekToken();
                }
                else
                {
                    Throw(tok.Start, "Did not find the expected ',' or ']'");
                }
            }

            if (tok.Kind == TokenKind.Key)
            {
                int start = tok.Start;
                SkipToken();
                _state = ParserState.FlowSequenceEntryMappingKey;
                return EmitStart(YamlTokenType.StartMapping, YamlCollectionStyle.Flow, start);
            }
            if (tok.Kind == TokenKind.Value)
            {
                _state = ParserState.FlowSequenceEntryMappingKey;
                return EmitStart(YamlTokenType.StartMapping, YamlCollectionStyle.Flow, tok.Start);
            }
            if (tok.Kind != TokenKind.FlowSequenceEnd)
            {
                PushState(ParserState.FlowSequenceEntry);
                return ParseNode(block: false, indentless: false);
            }
        }

        int pos = tok.Start;
        PopState();
        SkipToken();
        return EmitEnd(YamlTokenType.EndSequence, pos);
    }

    private bool ParseFlowSequenceEntryMappingKey()
    {
        ref Token tok = ref PeekToken();
        if (tok.Kind != TokenKind.Value && tok.Kind != TokenKind.FlowEntry && tok.Kind != TokenKind.FlowSequenceEnd)
        {
            PushState(ParserState.FlowSequenceEntryMappingValue);
            return ParseNode(block: false, indentless: false);
        }
        _state = ParserState.FlowSequenceEntryMappingValue;
        return EmitEmptyScalar(tok.Start);
    }

    private bool ParseFlowSequenceEntryMappingValue()
    {
        ref Token tok = ref PeekToken();
        if (tok.Kind == TokenKind.Value)
        {
            int start = tok.Start;
            SkipToken();
            tok = ref PeekToken();
            if (tok.Kind != TokenKind.FlowEntry && tok.Kind != TokenKind.FlowSequenceEnd)
            {
                PushState(ParserState.FlowSequenceEntryMappingEnd);
                return ParseNode(block: false, indentless: false);
            }
            _state = ParserState.FlowSequenceEntryMappingEnd;
            return EmitEmptyScalar(start);
        }
        _state = ParserState.FlowSequenceEntryMappingEnd;
        return EmitEmptyScalar(tok.Start);
    }

    private bool ParseFlowMappingKey(bool first)
    {
        if (first)
        {
            PeekToken();
            SkipToken(); // FLOW-MAPPING-START
        }

        ref Token tok = ref PeekToken();
        if (tok.Kind != TokenKind.FlowMappingEnd)
        {
            if (!first)
            {
                if (tok.Kind == TokenKind.FlowEntry)
                {
                    SkipToken();
                    tok = ref PeekToken();
                }
                else
                {
                    Throw(tok.Start, "Did not find the expected ',' or '}'");
                }
            }

            if (tok.Kind == TokenKind.Key)
            {
                int start = tok.Start;
                SkipToken();
                tok = ref PeekToken();
                if (tok.Kind != TokenKind.Value && tok.Kind != TokenKind.FlowEntry && tok.Kind != TokenKind.FlowMappingEnd)
                {
                    PushState(ParserState.FlowMappingValue);
                    return ParseNode(block: false, indentless: false);
                }
                _state = ParserState.FlowMappingValue;
                return EmitEmptyScalar(start);
            }
            if (tok.Kind == TokenKind.Value)
            {
                _state = ParserState.FlowMappingValue;
                return EmitEmptyScalar(tok.Start);
            }
            if (tok.Kind != TokenKind.FlowMappingEnd)
            {
                PushState(ParserState.FlowMappingEmptyValue);
                return ParseNode(block: false, indentless: false);
            }
        }

        int pos = tok.Start;
        PopState();
        SkipToken();
        return EmitEnd(YamlTokenType.EndMapping, pos);
    }

    private bool ParseFlowMappingValue(bool empty)
    {
        ref Token tok = ref PeekToken();
        if (empty)
        {
            _state = ParserState.FlowMappingKey;
            return EmitEmptyScalar(tok.Start);
        }

        if (tok.Kind == TokenKind.Value)
        {
            int start = tok.Start;
            SkipToken();
            tok = ref PeekToken();
            if (tok.Kind != TokenKind.FlowEntry && tok.Kind != TokenKind.FlowMappingEnd)
            {
                PushState(ParserState.FlowMappingKey);
                return ParseNode(block: false, indentless: false);
            }
            _state = ParserState.FlowMappingKey;
            return EmitEmptyScalar(start);
        }

        _state = ParserState.FlowMappingKey;
        return EmitEmptyScalar(tok.Start);
    }
}
