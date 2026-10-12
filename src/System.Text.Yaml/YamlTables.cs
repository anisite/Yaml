namespace System.Text.Yaml;

/// <summary>256-entry byte classification tables used by the scanner's hot loops.</summary>
internal static class YamlTables
{
    /// <summary>Plain scalar scanning in block context: 1 = whitespace/break/EOF stop, 2 = ':' (conditional), 0 = continue.</summary>
    public static readonly byte[] PlainStopBlock = BuildPlainStop(flow: false);

    /// <summary>Plain scalar scanning in flow context: as block, plus 3 = flow indicator (always stops).</summary>
    public static readonly byte[] PlainStopFlow = BuildPlainStop(flow: true);

    /// <summary>0 = the byte always starts a plain scalar; 1 = needs the full token dispatch.</summary>
    public static readonly byte[] TokenStart = BuildTokenStart();

    private static byte[] BuildPlainStop(bool flow)
    {
        var t = new byte[256];
        t[0] = 1;
        t[(byte)' '] = 1;
        t[(byte)'\t'] = 1;
        t[(byte)'\n'] = 1;
        t[(byte)'\r'] = 1;
        t[(byte)':'] = 2;
        if (flow)
        {
            foreach (char c in ",[]{}") t[c] = 3;
        }
        return t;
    }

    private static byte[] BuildTokenStart()
    {
        var t = new byte[256];
        for (int i = 0; i <= 0x20; i++) t[i] = 1;
        foreach (char c in "%-.[]{},?:*&!|>'\"#@`") t[c] = 1;
        return t;
    }
}
