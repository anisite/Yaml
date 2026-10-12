using System.Text.Json;

namespace System.Text.Yaml;

public readonly partial struct YamlElement
{
    /// <summary>Name of the synthetic JSON property that carries the tag of a mapped collection (see <see cref="WriteAsJsonStrings"/>).</summary>
    internal static ReadOnlySpan<byte> TagPropertyName => "$yaml_tag"u8;

    /// <summary>Name of the synthetic JSON property that carries the content of a mapped collection.</summary>
    internal static ReadOnlySpan<byte> TagValuePropertyName => "$yaml_value"u8;

    /// <summary>
    /// Writes this node as JSON for the YAML serializer: every scalar is a JSON <em>string</em> (its exact text, so that
    /// <c>01</c> stays <c>"01"</c> for a string member and can still be parsed by a numeric member), except the scalars
    /// that resolve to null, which become JSON <c>null</c>. Aliases are expanded (bounded by <paramref name="budget"/>).
    /// A mapping tagged with one of <paramref name="mappedTags"/> is wrapped as
    /// <c>{"$yaml_tag":"&lt;tag&gt;","$yaml_value":{…}}</c>.
    /// </summary>
    internal void WriteAsJsonStrings(Utf8JsonWriter w, ref long budget, HashSet<string>? mappedTags)
    {
        if (--budget < 0) ThrowHelper.ThrowInvalidOperation("The node expansion limit was exceeded while converting to JSON.");

        YamlDocument doc = Doc;
        uint info = doc.InfoOf(_idx);
        switch (RowInfo.Type(info))
        {
            case RowInfo.Mapping:
            {
                string? tag = null;
                if (mappedTags is not null && RowInfo.Tag(info) == YamlTagKind.Other)
                {
                    tag = doc.GetTagString(_idx);
                    if (tag is not null && !mappedTags.Contains(tag)) tag = null;
                }
                if (tag is not null)
                {
                    w.WriteStartObject();
                    w.WriteString(TagPropertyName, tag);
                    w.WritePropertyName(TagValuePropertyName);
                }
                w.WriteStartObject();
                foreach (YamlEntry e in EnumerateMapping())
                {
                    if (RowInfo.Type(e.Key.Info) == RowInfo.Scalar)
                    {
                        w.WritePropertyName(e.Key._doc.ScalarBytes(e.Key._idx));
                    }
                    else
                    {
                        // A collection used as a key (typically an accident such as `fr: {{expr}}`) cannot be a JSON name:
                        // it is bound under its description ("Mapping[1]", "Sequence[2]") instead of failing the whole document.
                        w.WritePropertyName(e.Key.ToString());
                    }
                    e.Value.WriteAsJsonStrings(w, ref budget, mappedTags);
                }
                w.WriteEndObject();
                if (tag is not null) w.WriteEndObject();
                break;
            }
            case RowInfo.Sequence:
                w.WriteStartArray();
                foreach (YamlElement item in EnumerateSequence())
                {
                    item.WriteAsJsonStrings(w, ref budget, mappedTags);
                }
                w.WriteEndArray();
                break;
            default:
                if (doc.ScalarKindOf(_idx) == YamlScalarKind.Null) w.WriteNullValue();
                else w.WriteStringValue(doc.ScalarBytes(_idx));
                break;
        }
    }
}
