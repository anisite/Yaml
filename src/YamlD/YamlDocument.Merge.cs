namespace YamlD;

public sealed partial class YamlDocument
{
    /// <summary>
    /// Finds the value of the scalar key <paramref name="key"/> in a mapping. Explicit entries win over entries
    /// merged through <c>&lt;&lt;</c>; among merge sources, earlier ones win (YAML 1.1 merge key type).
    /// With duplicate keys allowed, the last explicit occurrence wins.
    /// </summary>
    internal bool TryFindValue(int map, ReadOnlySpan<byte> key, out int valueRow)
    {
        DbRow[] rows = _rows!;
        int pairs = rows[map].Length;
        int found = -1;
        int k = map + 1;
        for (int i = 0; i < pairs; i++)
        {
            int v = k + rows[k].Rows;
            int kr = ResolveAlias(k);
            ref DbRow keyRow = ref rows[kr];
            if ((keyRow.Info & (RowInfo.TypeMask | RowInfo.IsMergeKey)) == RowInfo.Scalar && ScalarBytes(kr).SequenceEqual(key))
            {
                found = v;
            }
            k = v + rows[v].Rows;
        }

        if (found >= 0)
        {
            valueRow = found;
            return true;
        }

        if ((rows[map].Info & RowInfo.HasMerge) != 0)
        {
            k = map + 1;
            for (int i = 0; i < pairs; i++)
            {
                int v = k + rows[k].Rows;
                int kr = ResolveAlias(k);
                if ((rows[kr].Info & RowInfo.IsMergeKey) != 0)
                {
                    int src = ResolveAlias(v);
                    if (RowInfo.Type(rows[src].Info) == RowInfo.Mapping)
                    {
                        if (TryFindValue(src, key, out valueRow)) return true;
                    }
                    else
                    {
                        int count = rows[src].Length;
                        int child = src + 1;
                        for (int c = 0; c < count; c++)
                        {
                            int m = ResolveAlias(child);
                            if (TryFindValue(m, key, out valueRow)) return true;
                            child += rows[child].Rows;
                        }
                    }
                }
                k = v + rows[v].Rows;
            }
        }

        valueRow = -1;
        return false;
    }

    /// <summary>
    /// The effective [key0, value0, key1, value1, ...] rows of a mapping: explicit entries in source order, with
    /// every merge key replaced in place by the merged entries that are not overridden.
    /// </summary>
    internal List<int> EffectivePairs(int map)
    {
        var result = new List<int>();
        CollectEffective(map, result);
        return result;
    }

    private void CollectEffective(int map, List<int> result)
    {
        DbRow[] rows = _rows!;
        int pairs = rows[map].Length;

        // Rows of the explicit (non-merge) keys of this mapping: they override anything merged.
        var explicitKeys = new List<int>();
        int k = map + 1;
        for (int i = 0; i < pairs; i++)
        {
            int v = k + rows[k].Rows;
            int kr = ResolveAlias(k);
            if ((rows[kr].Info & RowInfo.IsMergeKey) == 0) explicitKeys.Add(kr);
            k = v + rows[v].Rows;
        }

        var seenMerged = new List<int>();
        k = map + 1;
        for (int i = 0; i < pairs; i++)
        {
            int v = k + rows[k].Rows;
            int kr = ResolveAlias(k);
            if ((rows[kr].Info & RowInfo.IsMergeKey) == 0)
            {
                result.Add(k);
                result.Add(v);
            }
            else
            {
                int src = ResolveAlias(v);
                if (RowInfo.Type(rows[src].Info) == RowInfo.Mapping)
                {
                    AddMerged(src, explicitKeys, seenMerged, result);
                }
                else
                {
                    int count = rows[src].Length;
                    int child = src + 1;
                    for (int c = 0; c < count; c++)
                    {
                        AddMerged(ResolveAlias(child), explicitKeys, seenMerged, result);
                        child += rows[child].Rows;
                    }
                }
            }
            k = v + rows[v].Rows;
        }
    }

    private void AddMerged(int source, List<int> explicitKeys, List<int> seenMerged, List<int> result)
    {
        DbRow[] rows = _rows!;
        var sub = new List<int>();
        if ((rows[source].Info & RowInfo.HasMerge) != 0) CollectEffective(source, sub);
        else
        {
            int pairs = rows[source].Length;
            int k = source + 1;
            for (int i = 0; i < pairs; i++)
            {
                int v = k + rows[k].Rows;
                sub.Add(k);
                sub.Add(v);
                k = v + rows[v].Rows;
            }
        }

        for (int i = 0; i < sub.Count; i += 2)
        {
            int kr = ResolveAlias(sub[i]);
            if (RowInfo.Type(rows[kr].Info) == RowInfo.Scalar)
            {
                if (ContainsKey(explicitKeys, kr) || ContainsKey(seenMerged, kr)) continue;
                seenMerged.Add(kr);
            }
            result.Add(sub[i]);
            result.Add(sub[i + 1]);
        }
    }

    private bool ContainsKey(List<int> keys, int keyRow)
    {
        for (int i = 0; i < keys.Count; i++)
        {
            int other = keys[i];
            if (RowInfo.Type(_rows![other].Info) == RowInfo.Scalar && KeysEqual(other, keyRow)) return true;
        }
        return false;
    }
}
