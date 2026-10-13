using System.Buffers;
using System.Runtime.CompilerServices;

namespace YamlD;

public sealed partial class YamlDocument
{
    // ---------------------------------------------------------------------------------------------
    // Public entry points
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Parses a stream containing exactly one YAML document. The memory is referenced, not copied
    /// (unless it is UTF-16/UTF-32 and must be transcoded), so it must stay unchanged while the document is alive.
    /// </summary>
    /// <exception cref="YamlException">Malformed YAML, or the stream holds more than one document.</exception>
    public static YamlDocument Parse(ReadOnlyMemory<byte> utf8Yaml, YamlDocumentOptions options = default)
    {
        ReadOnlyMemory<byte> data = Normalize(utf8Yaml, out DataOwner? owner);
        return ParseSingle(data, owner, options);
    }

    /// <summary>Parses a string containing exactly one YAML document.</summary>
    public static YamlDocument Parse(string yaml, YamlDocumentOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        int max = Encoding.UTF8.GetMaxByteCount(yaml.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(max);
        int n = Encoding.UTF8.GetBytes(yaml, rented);
        var owner = new DataOwner(rented);
        owner.AddRef();
        try
        {
            return ParseSingle(rented.AsMemory(0, n), owner, options);
        }
        finally
        {
            owner.Release();
        }
    }

    /// <summary>Parses a stream (any YAML encoding) containing exactly one YAML document.</summary>
    public static YamlDocument Parse(Stream stream, YamlDocumentOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var owner = ReadAll(stream, out int length);
        return FinishStream(owner, length, options, all: false)[0];
    }

    /// <summary>Asynchronously parses a stream containing exactly one YAML document.</summary>
    public static async Task<YamlDocument> ParseAsync(Stream stream, YamlDocumentOptions options = default, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var (owner, length) = await ReadAllAsync(stream, cancellationToken).ConfigureAwait(false);
        return FinishStream(owner, length, options, all: false)[0];
    }

    /// <summary>Parses every document of a multi-document stream. Dispose each returned document.</summary>
    public static List<YamlDocument> ParseAll(ReadOnlyMemory<byte> utf8Yaml, YamlDocumentOptions options = default)
    {
        ReadOnlyMemory<byte> data = Normalize(utf8Yaml, out DataOwner? owner);
        return ParseMany(data, owner, options);
    }

    /// <summary>Parses every document of a multi-document string.</summary>
    public static List<YamlDocument> ParseAll(string yaml, YamlDocumentOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        int max = Encoding.UTF8.GetMaxByteCount(yaml.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(max);
        int n = Encoding.UTF8.GetBytes(yaml, rented);
        var owner = new DataOwner(rented);
        owner.AddRef();
        try
        {
            return ParseMany(rented.AsMemory(0, n), owner, options);
        }
        finally
        {
            owner.Release();
        }
    }

    /// <summary>Parses every document of a multi-document stream.</summary>
    public static List<YamlDocument> ParseAll(Stream stream, YamlDocumentOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var owner = ReadAll(stream, out int length);
        return FinishStream(owner, length, options, all: true);
    }

    // ---------------------------------------------------------------------------------------------
    // Input plumbing
    // ---------------------------------------------------------------------------------------------

    private static ReadOnlyMemory<byte> Normalize(ReadOnlyMemory<byte> raw, out DataOwner? owner)
    {
        owner = null;
        YamlEncoding.ToUtf8(raw.Span, out byte[]? transcoded);
        if (transcoded is null) return raw;
        owner = new DataOwner(transcoded, pooled: false);
        return transcoded;
    }

    private static DataOwner ReadAll(Stream stream, out int length)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(stream.CanSeek ? (int)Math.Min(Math.Max(stream.Length - stream.Position, 0) + 1, int.MaxValue / 2) : 4096);
        int total = 0;
        try
        {
            for (;;)
            {
                if (total == buffer.Length)
                {
                    byte[] bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    Buffer.BlockCopy(buffer, 0, bigger, 0, total);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = bigger;
                }
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0) break;
                total += read;
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
        length = total;
        return new DataOwner(buffer);
    }

    private static async Task<(DataOwner, int)> ReadAllAsync(Stream stream, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(stream.CanSeek ? (int)Math.Min(Math.Max(stream.Length - stream.Position, 0) + 1, int.MaxValue / 2) : 4096);
        int total = 0;
        try
        {
            for (;;)
            {
                if (total == buffer.Length)
                {
                    byte[] bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    Buffer.BlockCopy(buffer, 0, bigger, 0, total);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = bigger;
                }
                int read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
        return (new DataOwner(buffer), total);
    }

    private static List<YamlDocument> FinishStream(DataOwner rawOwner, int length, YamlDocumentOptions options, bool all)
    {
        rawOwner.AddRef();
        DataOwner? transOwner = null;
        try
        {
            ReadOnlyMemory<byte> data = rawOwner.Array!.AsMemory(0, length);
            DataOwner effective = rawOwner;
            YamlEncoding.ToUtf8(data.Span, out byte[]? transcoded);
            if (transcoded is not null)
            {
                data = transcoded;
                transOwner = new DataOwner(transcoded, pooled: false);
                transOwner.AddRef();
                effective = transOwner;
            }
            return all ? ParseMany(data, effective, options) : [ParseSingle(data, effective, options)];
        }
        finally
        {
            transOwner?.Release();
            rawOwner.Release();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Core
    // ---------------------------------------------------------------------------------------------

    private static YamlDocument ParseSingle(ReadOnlyMemory<byte> data, DataOwner? owner, YamlDocumentOptions options)
    {
        owner?.AddRef();
        try
        {
            var reader = new Utf8YamlReader(data.Span, options.ToReaderOptions());
            try
            {
                YamlDocument? doc = BuildNext(ref reader, data, owner, options, single: true);
                if (doc is null)
                {
                    // Empty stream: a document holding a single empty (null) scalar.
                    doc = new YamlDocument(data, owner, options.Schema);
                    doc.InitRows(16);
                    doc.AddRow(RowInfo.Scalar, 0, 0, 1);
                    return doc;
                }

                // Make sure no second document follows.
                bool more;
                try
                {
                    more = reader.Read();
                }
                catch
                {
                    doc.Dispose();
                    throw;
                }
                if (more)
                {
                    doc.Dispose();
                    throw YamlException.At(data.Span, (int)reader.TokenStartIndex, "The stream contains more than one document; use ParseAll");
                }
                return doc;
            }
            finally
            {
                reader.Dispose();
            }
        }
        finally
        {
            owner?.Release();
        }
    }

    private static List<YamlDocument> ParseMany(ReadOnlyMemory<byte> data, DataOwner? owner, YamlDocumentOptions options)
    {
        owner?.AddRef();
        var docs = new List<YamlDocument>();
        try
        {
            var reader = new Utf8YamlReader(data.Span, options.ToReaderOptions());
            try
            {
                for (;;)
                {
                    YamlDocument? doc = BuildNext(ref reader, data, owner, options, single: false);
                    if (doc is null) break;
                    docs.Add(doc);
                }
            }
            catch
            {
                foreach (YamlDocument d in docs) d.Dispose();
                throw;
            }
            finally
            {
                reader.Dispose();
            }
            return docs;
        }
        finally
        {
            owner?.Release();
        }
    }

    private void InitRows(int capacity)
    {
        _rows = ArrayPool<DbRow>.Shared.Rent(capacity);
    }

    private struct Frame
    {
        public int Row;
        public int Items;
        public bool IsMap;
        public bool PendingMerge;
        public bool MergeValue;
        public bool Complex;
        public bool HasMerge;
    }

    /// <summary>Reads the next document of the stream, or returns null at its end.</summary>
    private static YamlDocument? BuildNext(ref Utf8YamlReader reader, ReadOnlyMemory<byte> data, DataOwner? owner, in YamlDocumentOptions opts, bool single)
    {
        if (!reader.Read()) return null;

        var doc = new YamlDocument(data, owner, opts.Schema);
        Frame[]? frames = null;
        AnchorMap? anchors = null;
        try
        {
            // Single documents: size for the whole stream. Multi-document streams: start small, rows grow geometrically.
            doc.InitRows(single ? Math.Max(16, data.Length / 10) : 256);
            doc.Version = reader.Version;
            doc.HasExplicitStart = reader.IsExplicit;

            frames = ArrayPool<Frame>.Shared.Rent(32);
            int top = -1;
            bool mergeEnabled = !opts.MergeKeysDisabled;
            bool dupCheck = opts.DuplicateKeys == YamlDuplicateKeyHandling.Error;
            bool done = false;

            while (!done && reader.Read())
            {
                switch (reader.TokenType)
                {
                    case YamlTokenType.EndDocument:
                        done = true;
                        break;

                    case YamlTokenType.Scalar:
                    {
                        uint info = RowInfo.Scalar | ((uint)reader.ScalarStyle << RowInfo.StyleShift);
                        ReadOnlySpan<byte> value = reader.ValueSpan;
                        int loc;
                        if (reader.ValueIsInInput)
                        {
                            loc = reader.ValueOffset;
                        }
                        else
                        {
                            loc = doc.CopyToPool(value);
                            info |= RowInfo.InPool;
                        }

                        bool props = reader.HasAnchor || reader.Tag != YamlTagKind.None;
                        if (props) info |= RowInfo.HasProps | ((uint)reader.Tag << RowInfo.TagShift);

                        if (top >= 0)
                        {
                            ref Frame p = ref frames[top];
                            if (p.IsMap && (p.Items & 1) == 0)
                            {
                                if (mergeEnabled && IsMergeKeyScalar(value, reader.ScalarStyle, reader.Tag))
                                {
                                    info |= RowInfo.IsMergeKey;
                                    p.PendingMerge = true;
                                    p.HasMerge = true;
                                }
                            }
                            else if (p.IsMap && p.PendingMerge)
                            {
                                throw YamlException.At(data.Span, (int)reader.TokenStartIndex,
                                    "The value of a merge key (<<) must be a mapping or a sequence of mappings");
                            }
                            p.Items++;
                        }

                        int row = doc.AddRow(info, loc, value.Length, 1);
                        if (props)
                        {
                            doc.AddProps(row, ref reader);
                            if (reader.HasAnchor)
                            {
                                anchors ??= new AnchorMap();
                                anchors.Set(data.Span, reader.AnchorOffset, reader.AnchorLength, row);
                            }
                        }
                        break;
                    }

                    case YamlTokenType.Alias:
                    {
                        ReadOnlySpan<byte> name = reader.ValueSpan;
                        int target = anchors is null ? -1 : anchors.Get(data.Span, name);
                        if (target < 0)
                        {
                            throw YamlException.At(data.Span, (int)reader.TokenStartIndex,
                                "The alias refers to an anchor that was not defined");
                        }
                        if (doc._rows![target].Rows == 0)
                        {
                            throw YamlException.At(data.Span, (int)reader.TokenStartIndex,
                                "The alias refers to a node that is still being defined (recursive structures are not supported)");
                        }

                        bool mergeValue = false;
                        if (top >= 0)
                        {
                            ref Frame p = ref frames[top];
                            if (p.IsMap && (p.Items & 1) == 1 && p.PendingMerge)
                            {
                                mergeValue = true;
                                p.PendingMerge = false;
                            }
                            p.Items++;
                        }
                        if (mergeValue && !doc.IsValidMergeValue(target))
                        {
                            throw YamlException.At(data.Span, (int)reader.TokenStartIndex,
                                "The value of a merge key (<<) must be a mapping or a sequence of mappings");
                        }

                        doc.AddRow(RowInfo.Alias, target, 0, 1);
                        break;
                    }

                    case YamlTokenType.StartMapping:
                    case YamlTokenType.StartSequence:
                    {
                        bool isMap = reader.TokenType == YamlTokenType.StartMapping;
                        uint info = isMap ? RowInfo.Mapping : RowInfo.Sequence;
                        if (reader.CollectionStyle == YamlCollectionStyle.Flow) info |= RowInfo.Flow;
                        bool props = reader.HasAnchor || reader.Tag != YamlTagKind.None;
                        if (props) info |= RowInfo.HasProps | ((uint)reader.Tag << RowInfo.TagShift);

                        bool mergeValue = false;
                        if (top >= 0)
                        {
                            ref Frame p = ref frames[top];
                            p.Complex = true;
                            if (p.IsMap && (p.Items & 1) == 1 && p.PendingMerge)
                            {
                                mergeValue = true;
                                p.PendingMerge = false;
                            }
                            p.Items++;
                        }

                        int row = doc.AddRow(info, 0, 0, 0);
                        if (props)
                        {
                            doc.AddProps(row, ref reader);
                            if (reader.HasAnchor)
                            {
                                anchors ??= new AnchorMap();
                                anchors.Set(data.Span, reader.AnchorOffset, reader.AnchorLength, row);
                            }
                        }

                        top++;
                        if (top == frames.Length)
                        {
                            Frame[] bigger = ArrayPool<Frame>.Shared.Rent(frames.Length * 2);
                            Array.Copy(frames, bigger, top);
                            ArrayPool<Frame>.Shared.Return(frames);
                            frames = bigger;
                        }
                        frames[top] = new Frame { Row = row, IsMap = isMap, MergeValue = mergeValue };
                        break;
                    }

                    case YamlTokenType.EndMapping:
                    case YamlTokenType.EndSequence:
                    {
                        Frame f = frames[top--];
                        ref DbRow r = ref doc._rows![f.Row];
                        r.Rows = doc._rowCount - f.Row;
                        r.Length = f.IsMap ? f.Items >> 1 : f.Items;
                        if (f.Complex) r.Info |= RowInfo.Complex;
                        if (f.HasMerge) r.Info |= RowInfo.HasMerge;

                        if (f.IsMap)
                        {
                            if (dupCheck && r.Length > 1) doc.CheckDuplicateKeys(f.Row, ref reader);
                        }
                        else if (f.MergeValue)
                        {
                            if (!doc.IsValidMergeValue(f.Row))
                            {
                                throw YamlException.At(data.Span, (int)reader.TokenStartIndex,
                                    "The value of a merge key (<<) must be a mapping or a sequence of mappings");
                            }
                        }
                        break;
                    }
                }
            }

            if (doc._rowCount == 0)
            {
                // A document without any content node: a single null scalar.
                doc.AddRow(RowInfo.Scalar, 0, 0, 1);
            }

            return doc;
        }
        catch
        {
            doc.Dispose();
            throw;
        }
        finally
        {
            if (frames is not null) ArrayPool<Frame>.Shared.Return(frames);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsMergeKeyScalar(ReadOnlySpan<byte> value, YamlScalarStyle style, YamlTagKind tag)
    {
        if (tag == YamlTagKind.Merge) return true;
        return tag == YamlTagKind.None && style == YamlScalarStyle.Plain && value.Length == 2
               && value[0] == (byte)'<' && value[1] == (byte)'<';
    }

    /// <summary>A merge value is a mapping, or a sequence whose items are all mappings (aliases are followed).</summary>
    private bool IsValidMergeValue(int row)
    {
        row = ResolveAlias(row);
        uint type = RowInfo.Type(InfoOf(row));
        if (type == RowInfo.Mapping) return true;
        if (type != RowInfo.Sequence) return false;

        int count = _rows![row].Length;
        int child = row + 1;
        for (int i = 0; i < count; i++)
        {
            int c = ResolveAlias(child);
            if (RowInfo.Type(InfoOf(c)) != RowInfo.Mapping) return false;
            child += _rows[child].Rows;
        }
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // Duplicate keys
    // ---------------------------------------------------------------------------------------------

    private void CheckDuplicateKeys(int mapRow, ref Utf8YamlReader reader)
    {
        int pairs = _rows![mapRow].Length;
        int k = mapRow + 1;

        if (pairs <= 16)
        {
            // Collect the scalar (non-merge) key rows once, then compare lengths before touching any bytes.
            Span<int> keyRows = stackalloc int[16];
            int n = 0;
            for (int i = 0; i < pairs; i++)
            {
                int v = k + _rows[k].Rows;
                int kr = ResolveAlias(k);
                uint info = _rows[kr].Info;
                if ((info & (RowInfo.TypeMask | RowInfo.IsMergeKey)) == RowInfo.Scalar) keyRows[n++] = kr;
                k = v + _rows[v].Rows;
            }

            for (int i = 0; i < n - 1; i++)
            {
                ref DbRow a = ref _rows[keyRows[i]];
                for (int j = i + 1; j < n; j++)
                {
                    ref DbRow b = ref _rows[keyRows[j]];
                    if (a.Length == b.Length && KeysEqual(keyRows[i], keyRows[j])) ThrowDuplicate(keyRows[j], ref reader);
                }
            }
            return;
        }

        int size = 16;
        while (size < pairs * 2) size <<= 1;
        int[] table = ArrayPool<int>.Shared.Rent(size);
        Array.Clear(table, 0, size);
        try
        {
            int mask = size - 1;
            for (int i = 0; i < pairs; i++)
            {
                int v = k + _rows[k].Rows;
                if (IsPlainKeyRow(k))
                {
                    int kr = ResolveAlias(k);
                    int h = KeyHash.Compute(ScalarBytes(kr));
                    int slot = h & mask;
                    for (;;)
                    {
                        int occupant = table[slot];
                        if (occupant == 0)
                        {
                            table[slot] = k + 1;
                            break;
                        }
                        if (KeysEqual(occupant - 1, k)) ThrowDuplicate(k, ref reader);
                        slot = (slot + 1) & mask;
                    }
                }
                k = v + _rows[v].Rows;
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(table);
        }
    }

    /// <summary>Scalar (or alias to a scalar) key that is not a merge key.</summary>
    private bool IsPlainKeyRow(int row)
    {
        int r = ResolveAlias(row);
        uint info = _rows![r].Info;
        return RowInfo.Type(info) == RowInfo.Scalar && (info & RowInfo.IsMergeKey) == 0;
    }

    private bool KeysEqual(int a, int b)
    {
        a = ResolveAlias(a);
        b = ResolveAlias(b);
        if (!ScalarBytes(a).SequenceEqual(ScalarBytes(b))) return false;
        return ScalarKindOf(a) == ScalarKindOf(b);
    }

    private void ThrowDuplicate(int keyRow, ref Utf8YamlReader reader)
    {
        string key = Encoding.UTF8.GetString(ScalarBytes(ResolveAlias(keyRow)));
        throw YamlException.At(_data.Span, (int)reader.TokenStartIndex, $"Duplicate mapping key '{key}'");
    }

    internal YamlScalarKind ScalarKindOf(int row)
    {
        uint info = _rows![row].Info;
        return YamlScalar.Resolve(ScalarBytes(row), RowInfo.Style(info), RowInfo.Tag(info), _schema);
    }
}
