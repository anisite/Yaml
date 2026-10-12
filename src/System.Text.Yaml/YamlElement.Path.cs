namespace System.Text.Yaml;

public readonly partial struct YamlElement
{
    internal YamlDocument Document => Doc;

    /// <summary>
    /// Finds a node from a <c>System.Text.Json</c> error path (<c>$.a.b[2]</c>, <c>$['odd name']</c>) and returns a byte offset in the
    /// input that locates it: the key of the last mapping step when there is one, else the first located scalar of the node.
    /// Returns -1 when the path cannot be followed. Used to put a line and column on binding errors.
    /// </summary>
    internal int FindOffset(string path)
    {
        if (path.Length == 0 || path[0] != '$') return -1;

        YamlElement current = this;
        int hint = Doc.FirstLocation(_idx);
        int i = 1;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                int start = ++i;
                while (i < path.Length && path[i] != '.' && path[i] != '[') i++;
                if (!TryStep(ref current, path.AsSpan(start, i - start), ref hint)) return hint;
            }
            else if (path[i] == '[')
            {
                int close = path.IndexOf(']', i);
                if (close < 0) return hint;
                ReadOnlySpan<char> inner = path.AsSpan(i + 1, close - i - 1);
                i = close + 1;
                if (inner.Length >= 2 && inner[0] == '\'' && inner[^1] == '\'')
                {
                    if (!TryStep(ref current, inner[1..^1], ref hint)) return hint;
                }
                else if (int.TryParse(inner, out int index) && current.ValueKind == YamlValueKind.Sequence && index >= 0 && index < current.Count)
                {
                    current = current[index];
                    int h = current.Doc.FirstLocation(current._idx);
                    if (h >= 0) hint = h;
                }
                else
                {
                    return hint;
                }
            }
            else
            {
                return hint;
            }
        }
        return hint;
    }

    private static bool TryStep(ref YamlElement current, ReadOnlySpan<char> name, ref int hint)
    {
        if (current.ValueKind != YamlValueKind.Mapping) return false;

        bool found = false;
        YamlElement value = default;
        YamlElement key = default;
        foreach (YamlEntry e in current.EnumerateMapping())
        {
            if (RowInfo.Type(e.Key.Info) == RowInfo.Scalar && e.Key.ToString().AsSpan().SequenceEqual(name))
            {
                key = e.Key;
                value = e.Value;
                found = true; // the last occurrence wins, like the binder
            }
        }
        if (!found) return false;

        // Prefer the value (an invalid scalar, or the first scalar of an invalid collection); fall back to the key.
        int h = value.Doc.FirstLocation(value._idx);
        if (h < 0) h = key.Doc.FirstLocation(key._idx);
        if (h >= 0) hint = h;
        current = value;
        return true;
    }
}
