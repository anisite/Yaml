using System.Text.Json;

namespace System.Text.Yaml.Serialization;

/// <summary>Ready-made naming policies for YAML member names.</summary>
public static class YamlNamingPolicy
{
    /// <summary>
    /// <c>camelCase</c> like YamlDotNet's <c>CamelCaseNamingConvention</c>: underscores are removed (the letter after one is
    /// upper-cased) and only the first character is lower-cased (<c>URL</c> becomes <c>uRL</c>, <c>IdSysteme</c> becomes <c>idSysteme</c>).
    /// This differs from <see cref="JsonNamingPolicy.CamelCase"/>, which lower-cases a whole leading run of capitals.
    /// </summary>
    public static JsonNamingPolicy CamelCase { get; } = new CamelCasePolicy();

    private sealed class CamelCasePolicy : JsonNamingPolicy
    {
        public override string ConvertName(string name)
        {
            if (name.Length == 0) return name;

            bool simple = char.IsLower(name[0]) || !char.IsLetter(name[0]);
            if (simple && name.AsSpan().IndexOf('_') < 0) return name;

            var sb = new StringBuilder(name.Length);
            bool upperNext = true;
            foreach (char c in name)
            {
                if (c == '_')
                {
                    upperNext = true;
                }
                else if (upperNext)
                {
                    sb.Append(char.ToUpperInvariant(c));
                    upperNext = false;
                }
                else
                {
                    sb.Append(c);
                }
            }

            if (sb.Length > 0) sb[0] = char.ToLowerInvariant(sb[0]);
            return sb.ToString();
        }
    }
}
