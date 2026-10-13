using System.Text;
using YamlD;

namespace YamlD.Tests;

/// <summary>Renders the reader's events in the line format used by the official yaml-test-suite (test.event).</summary>
public static class EventDump
{
    public static string Dump(ReadOnlySpan<byte> yaml)
    {
        var sb = new StringBuilder();
        sb.Append("+STR\n");
        var reader = new Utf8YamlReader(yaml);
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case YamlTokenType.StartDocument:
                        sb.Append(reader.IsExplicit ? "+DOC ---\n" : "+DOC\n");
                        break;
                    case YamlTokenType.EndDocument:
                        sb.Append(reader.IsExplicit ? "-DOC ...\n" : "-DOC\n");
                        break;
                    case YamlTokenType.StartMapping:
                        sb.Append("+MAP");
                        if (reader.CollectionStyle == YamlCollectionStyle.Flow) sb.Append(" {}");
                        AppendProps(sb, ref reader);
                        sb.Append('\n');
                        break;
                    case YamlTokenType.EndMapping:
                        sb.Append("-MAP\n");
                        break;
                    case YamlTokenType.StartSequence:
                        sb.Append("+SEQ");
                        if (reader.CollectionStyle == YamlCollectionStyle.Flow) sb.Append(" []");
                        AppendProps(sb, ref reader);
                        sb.Append('\n');
                        break;
                    case YamlTokenType.EndSequence:
                        sb.Append("-SEQ\n");
                        break;
                    case YamlTokenType.Alias:
                        sb.Append("=ALI *").Append(Encoding.UTF8.GetString(reader.ValueSpan)).Append('\n');
                        break;
                    case YamlTokenType.Scalar:
                        sb.Append("=VAL");
                        AppendProps(sb, ref reader);
                        sb.Append(' ').Append(reader.ScalarStyle switch
                        {
                            YamlScalarStyle.Plain => ':',
                            YamlScalarStyle.SingleQuoted => '\'',
                            YamlScalarStyle.DoubleQuoted => '"',
                            YamlScalarStyle.Literal => '|',
                            _ => '>',
                        });
                        Escape(sb, Encoding.UTF8.GetString(reader.ValueSpan));
                        sb.Append('\n');
                        break;
                }
            }
        }
        finally
        {
            reader.Dispose();
        }
        sb.Append("-STR\n");
        return sb.ToString();
    }

    private static void AppendProps(StringBuilder sb, ref Utf8YamlReader reader)
    {
        if (reader.HasAnchor) sb.Append(" &").Append(Encoding.UTF8.GetString(reader.Anchor));
        string? tag = reader.GetTag();
        if (tag is not null) sb.Append(" <").Append(tag).Append('>');
    }

    private static void Escape(StringBuilder sb, string s)
    {
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\r': sb.Append("\\r"); break;
                case '\b': sb.Append("\\b"); break;
                case '\0': sb.Append("\\0"); break;
                default: sb.Append(c); break;
            }
        }
    }
}
