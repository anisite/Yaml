using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace YamlD;

/// <summary>Small allocation-light hash map from anchor name (a slice of the input) to the row it anchors.</summary>
internal sealed class AnchorMap
{
    private struct Entry
    {
        public int Hash;
        public int Start;
        public int Length;
        public int Row;
        public int Next;
    }

    private int[] _buckets = new int[16];
    private Entry[] _entries = new Entry[16];
    private int _count;

    public int Count => _count;

    public void Clear()
    {
        if (_count == 0) return;
        Array.Clear(_buckets);
        _count = 0;
    }

    public void Set(ReadOnlySpan<byte> data, int start, int length, int row)
    {
        ReadOnlySpan<byte> name = data.Slice(start, length);
        int hash = KeyHash.Compute(name);
        int bucket = hash & (_buckets.Length - 1);
        for (int i = _buckets[bucket] - 1; i >= 0; i = _entries[i].Next)
        {
            ref Entry e = ref _entries[i];
            if (e.Hash == hash && e.Length == length && data.Slice(e.Start, e.Length).SequenceEqual(name))
            {
                e.Row = row; // an anchor may be redefined: aliases refer to the most recent definition
                return;
            }
        }

        if (_count == _entries.Length)
        {
            Array.Resize(ref _entries, _count * 2);
            int[] nb = new int[_buckets.Length * 2];
            _buckets = nb;
            for (int i = 0; i < _count; i++)
            {
                int b = _entries[i].Hash & (nb.Length - 1);
                _entries[i].Next = nb[b] - 1;
                nb[b] = i + 1;
            }
            bucket = hash & (_buckets.Length - 1);
        }

        ref Entry n = ref _entries[_count];
        n.Hash = hash;
        n.Start = start;
        n.Length = length;
        n.Row = row;
        n.Next = _buckets[bucket] - 1;
        _buckets[bucket] = ++_count;
    }

    public int Get(ReadOnlySpan<byte> data, ReadOnlySpan<byte> name)
    {
        int hash = KeyHash.Compute(name);
        for (int i = _buckets[hash & (_buckets.Length - 1)] - 1; i >= 0; i = _entries[i].Next)
        {
            ref Entry e = ref _entries[i];
            if (e.Hash == hash && e.Length == name.Length && data.Slice(e.Start, e.Length).SequenceEqual(name))
            {
                return e.Row;
            }
        }
        return -1;
    }
}

internal static class KeyHash
{
    /// <summary>A fast, deterministic 32-bit hash for short byte strings (wyhash-style mixing).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Compute(ReadOnlySpan<byte> s)
    {
        ulong h = 0x9E3779B97F4A7C15UL ^ (ulong)s.Length;
        ref byte r = ref MemoryMarshal.GetReference(s);
        int i = 0;
        while (i + 8 <= s.Length)
        {
            ulong v = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref r, i));
            h = (h ^ v) * 0xFF51AFD7ED558CCDUL;
            h ^= h >> 32;
            i += 8;
        }
        if (i < s.Length)
        {
            ulong v = 0;
            int shift = 0;
            for (; i < s.Length; i++, shift += 8) v |= (ulong)Unsafe.Add(ref r, i) << shift;
            h = (h ^ v) * 0xC4CEB9FE1A85EC53UL;
            h ^= h >> 29;
        }
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 32;
        return (int)h & int.MaxValue;
    }
}
