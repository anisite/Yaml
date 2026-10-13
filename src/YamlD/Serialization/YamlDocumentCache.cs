using System.Collections.Concurrent;

namespace YamlD.Serialization;

/// <summary>
/// A bounded, thread-safe cache of parsed <see cref="YamlDocument"/>s, keyed by the exact YAML text and the parsing options.
/// </summary>
/// <remarks>
/// <para>
/// Parsing dominates the cost of reading YAML. This cache skips it when the same text is read again, while every caller still
/// receives its <b>own, freshly bound object</b>: nothing mutable is shared (the cached document is read-only after parsing and is
/// only read concurrently). Hits are verified byte-for-byte, so a hash collision can only cost a re-parse, never return a wrong result.
/// </para>
/// <para>
/// Assign an instance to <see cref="YamlSerializerOptions.DocumentCache"/>; one instance may be shared by several options.
/// Evicted documents are simply released to the garbage collector (they are never disposed, so a reader that still uses one is unaffected).
/// </para>
/// </remarks>
public sealed class YamlDocumentCache
{
    private readonly ConcurrentDictionary<Key, Entry> _entries = new();
    private readonly object _trimLock = new();
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private long _bytes;
    private long _tick;
    private long _hits;
    private long _misses;

    /// <param name="maxEntries">Maximum number of cached documents.</param>
    /// <param name="maxBytes">Approximate memory budget (the estimate is a multiple of the YAML size).</param>
    public YamlDocumentCache(int maxEntries = 256, long maxBytes = 32L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
    }

    /// <summary>Number of documents currently cached.</summary>
    public int Count => _entries.Count;

    /// <summary>Estimated memory held by the cached documents, in bytes.</summary>
    public long EstimatedBytes => Interlocked.Read(ref _bytes);

    /// <summary>Number of lookups served from the cache.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Number of lookups that had to parse.</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>Removes every cached document.</summary>
    public void Clear()
    {
        lock (_trimLock)
        {
            _entries.Clear();
            Interlocked.Exchange(ref _bytes, 0);
        }
    }

    /// <summary>Returns the cached document for this exact input, or parses it, caches it and returns it. Never dispose the result.</summary>
    internal YamlDocument GetOrParse(ReadOnlyMemory<byte> input, in YamlDocumentOptions options)
    {
        var key = new Key(Hash(input.Span), input.Length, options);

        if (_entries.TryGetValue(key, out Entry? entry) && input.Span.SequenceEqual(entry.Input))
        {
            entry.LastUsed = Interlocked.Increment(ref _tick);
            Interlocked.Increment(ref _hits);
            return entry.Document;
        }

        Interlocked.Increment(ref _misses);

        // The document references the cache's own copy of the text, so the caller's buffer may be reused or modified freely.
        byte[] copy = input.ToArray();
        YamlDocument doc = YamlDocument.Parse(copy, options);
        var created = new Entry(copy, doc, EstimateCost(copy.Length)) { LastUsed = Interlocked.Increment(ref _tick) };

        if (created.Cost <= _maxBytes)
        {
            lock (_trimLock)
            {
                if (_entries.TryGetValue(key, out Entry? old)) Interlocked.Add(ref _bytes, -old.Cost);
                _entries[key] = created;
                Interlocked.Add(ref _bytes, created.Cost);
                Trim();
            }
        }
        return doc;
    }

    private void Trim()
    {
        while (_entries.Count > _maxEntries || Interlocked.Read(ref _bytes) > _maxBytes)
        {
            Key? oldestKey = null;
            long oldest = long.MaxValue;
            foreach (KeyValuePair<Key, Entry> kv in _entries)
            {
                if (kv.Value.LastUsed < oldest)
                {
                    oldest = kv.Value.LastUsed;
                    oldestKey = kv.Key;
                }
            }
            if (oldestKey is null) return;
            if (_entries.TryRemove(oldestKey.Value, out Entry? removed)) Interlocked.Add(ref _bytes, -removed.Cost);
        }
    }

    // The document keeps rows (16 bytes per node, roughly one node per 8 bytes of text) plus the text itself.
    private static long EstimateCost(int length) => (long)length * 3 + 1024;

    private static int Hash(ReadOnlySpan<byte> data)
    {
        var h = new HashCode();
        h.AddBytes(data);
        return h.ToHashCode();
    }

    private readonly record struct Key(int Hash, int Length, int MaxDepth, bool SkipValidation, bool NoMerge, YamlDuplicateKeyHandling Duplicates, YamlSchema Schema)
    {
        public Key(int hash, int length, in YamlDocumentOptions o)
            : this(hash, length, o.MaxDepth, o.SkipInputValidation, o.MergeKeysDisabled, o.DuplicateKeys, o.Schema)
        {
        }
    }

    private sealed class Entry
    {
        public Entry(byte[] input, YamlDocument document, long cost)
        {
            Input = input;
            Document = document;
            Cost = cost;
        }

        public byte[] Input { get; }
        public YamlDocument Document { get; }
        public long Cost { get; }
        public long LastUsed;
    }
}
