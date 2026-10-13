// The reflection-based path is the default one; AOT/trimmed apps use the JsonTypeInfo<T> overloads (source generation).
#pragma warning disable IL2026, IL3050, IL2071
using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YamlD.Serialization;

/// <summary>
/// Builds the values of <c>object</c>-typed members: <c>null</c>, <see cref="string"/> (every scalar), <see cref="List{T}"/> of
/// <see cref="object"/> and <see cref="Dictionary{TKey, TValue}"/> (<c>object</c> or <c>string</c> keys), exactly like YamlDotNet does.
/// </summary>
internal sealed class YamlObjectConverter : JsonConverter<object?>
{
    private readonly bool _stringKeys;
    private readonly Dictionary<string, Type>? _tags;

    public YamlObjectConverter(bool stringKeys, Dictionary<string, Type>? tags)
    {
        _stringKeys = stringKeys;
        _tags = tags;
    }

    public override bool HandleNull => true;

    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReadValue(ref reader, options);

    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Writing YAML is not supported.");

    private object? ReadValue(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.StartArray:
            {
                var list = new List<object?>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    list.Add(ReadValue(ref reader, options));
                }
                return list;
            }
            case JsonTokenType.StartObject:
                return ReadObject(ref reader, options);
            default:
                throw new JsonException($"Unexpected token {reader.TokenType} while reading a YAML node.");
        }
    }

    private object? ReadObject(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        if (!reader.Read()) throw new JsonException("Unexpected end of data.");

        if (reader.TokenType == JsonTokenType.EndObject)
        {
            return NewDictionary();
        }

        // A mapping carrying a mapped tag is wrapped as {"$yaml_tag": …, "$yaml_value": {…}}.
        if (_tags is not null && reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals(YamlElement.TagPropertyName))
        {
            reader.Read();
            string tag = reader.GetString()!;
            reader.Read(); // "$yaml_value"
            reader.Read(); // start of the value
            object? value = _tags.TryGetValue(tag, out Type? type)
                ? JsonSerializer.Deserialize(ref reader, type, options)
                : ReadValue(ref reader, options);
            reader.Read(); // end of the wrapper
            return value;
        }

        if (_stringKeys)
        {
            var dict = new Dictionary<string, object?>();
            do
            {
                string key = reader.GetString()!;
                reader.Read();
                dict[key] = ReadValue(ref reader, options);
            }
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject);
            return dict;
        }
        else
        {
            var dict = new Dictionary<object, object?>();
            do
            {
                string key = reader.GetString()!;
                reader.Read();
                dict[key] = ReadValue(ref reader, options);
            }
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject);
            return dict;
        }
    }

    private object NewDictionary() => _stringKeys ? new Dictionary<string, object?>() : new Dictionary<object, object?>();
}

/// <summary>
/// <see cref="Dictionary{TKey, TValue}"/> / <see cref="IDictionary{TKey, TValue}"/> with <c>object</c> keys: the keys are the YAML key strings.
/// </summary>
internal sealed class YamlObjectKeyDictionaryConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        if (!typeToConvert.IsGenericType) return false;
        Type def = typeToConvert.GetGenericTypeDefinition();
        return (def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>))
            && typeToConvert.GetGenericArguments()[0] == typeof(object);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2055", Justification = "Dictionary<object, TValue> is rooted by the caller's own use of the type.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Dictionary<object, TValue> is rooted by the caller's own use of the type.")]
    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        Type valueType = typeToConvert.GetGenericArguments()[1];
        return (JsonConverter)Activator.CreateInstance(typeof(YamlObjectKeyDictionaryConverter<>).MakeGenericType(valueType))!;
    }
}

internal sealed class YamlObjectKeyDictionaryConverter<TValue> : JsonConverter<IDictionary<object, TValue>>
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert == typeof(Dictionary<object, TValue>) || typeToConvert == typeof(IDictionary<object, TValue>);

    public override IDictionary<object, TValue>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException($"A mapping is expected for {typeToConvert}.");

        var dict = new Dictionary<object, TValue>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string key = reader.GetString()!;
            reader.Read();
            dict[key] = JsonSerializer.Deserialize<TValue>(ref reader, options)!;
        }
        return dict;
    }

    public override void Write(Utf8JsonWriter writer, IDictionary<object, TValue> value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Writing YAML is not supported.");
}

/// <summary>Reads a <c>byte[]</c> from a hexadecimal string (an odd number of digits is left-padded with <c>0</c>).</summary>
internal sealed class YamlHexByteArrayConverter : JsonConverter<byte[]>
{
    public override byte[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("A hexadecimal string is expected for a byte array.");
        string hex = reader.GetString()!.Trim();
        if (hex.Length % 2 != 0) hex = "0" + hex;
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException ex)
        {
            throw new JsonException("The value is not a valid hexadecimal string.", ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Writing YAML is not supported.");
}

/// <summary>YAML-aware converters for the value types: a YAML null becomes <c>default</c>, text is parsed with YAML 1.1 + 1.2 rules.</summary>
internal static class YamlConverters
{
    public static void AddTo(IList<JsonConverter> converters)
    {
        converters.Add(new YamlObjectKeyDictionaryConverterFactory());
        converters.Add(new YamlEnumConverterFactory());
        converters.Add(new BooleanConverter());
        converters.Add(new Int32Converter());
        converters.Add(new Int64Converter());
        converters.Add(new Int16Converter());
        converters.Add(new SByteConverter());
        converters.Add(new ByteConverter());
        converters.Add(new UInt16Converter());
        converters.Add(new UInt32Converter());
        converters.Add(new UInt64Converter());
        converters.Add(new DoubleConverter());
        converters.Add(new SingleConverter());
        converters.Add(new DecimalConverter());
        converters.Add(new DateTimeConverter());
        converters.Add(new DateTimeOffsetConverter());
        converters.Add(new GuidConverter());
    }

    /// <summary>The UTF-8 text of the current JSON string token, without allocating when it is not escaped.</summary>
    internal static ReadOnlySpan<byte> Text(ref Utf8JsonReader reader, ref byte[]? rented, Type target)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"A scalar is expected for {target}, found {reader.TokenType}.");
        }
        if (!reader.HasValueSequence && !reader.ValueIsEscaped) return reader.ValueSpan;

        int max = reader.HasValueSequence ? checked((int)reader.ValueSequence.Length) : reader.ValueSpan.Length;
        rented = ArrayPool<byte>.Shared.Rent(max);
        int n = reader.CopyString(rented);
        return rented.AsSpan(0, n);
    }

    internal static void Return(byte[]? rented)
    {
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
    }

    internal static JsonException Bad(ReadOnlySpan<byte> text, Type target) =>
        new($"The value '{Encoding.UTF8.GetString(text)}' is not a valid {target.Name}.");

    private abstract class ScalarConverter<T> : JsonConverter<T>
    {
        public sealed override bool HandleNull => true;

        protected abstract bool TryParse(ReadOnlySpan<byte> text, out T value);

        public sealed override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return default;
            byte[]? rented = null;
            try
            {
                ReadOnlySpan<byte> text = Text(ref reader, ref rented, typeof(T));
                if (!TryParse(text, out T value)) throw Bad(text, typeof(T));
                return value;
            }
            finally
            {
                Return(rented);
            }
        }

        public sealed override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            throw new NotSupportedException("Writing YAML is not supported.");
    }

    private sealed class BooleanConverter : ScalarConverter<bool>
    {
        protected override bool TryParse(ReadOnlySpan<byte> text, out bool value)
        {
            Span<byte> lower = stackalloc byte[5];
            value = false;
            if (text.Length is 0 or > 5) return false;
            for (int i = 0; i < text.Length; i++)
            {
                byte b = text[i];
                lower[i] = b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b | 0x20) : b;
            }
            ReadOnlySpan<byte> t = lower.Slice(0, text.Length);
            if (t.SequenceEqual("true"u8) || t.SequenceEqual("yes"u8) || t.SequenceEqual("on"u8) || t.SequenceEqual("y"u8))
            {
                value = true;
                return true;
            }
            return t.SequenceEqual("false"u8) || t.SequenceEqual("no"u8) || t.SequenceEqual("off"u8) || t.SequenceEqual("n"u8);
        }
    }

    private static bool TryInt64(ReadOnlySpan<byte> text, long min, long max, out long value)
    {
        if (YamlScalar.TryParseInt64(text, out value, YamlSchema.Yaml11) && value >= min && value <= max) return true;
        value = 0;
        return false;
    }

    private sealed class Int32Converter : ScalarConverter<int>
    {
        protected override bool TryParse(ReadOnlySpan<byte> text, out int value)
        {
            bool ok = TryInt64(text, int.MinValue, int.MaxValue, out long l);
            value = (int)l;
            return ok;
        }
    }

    private sealed class Int64Converter : ScalarConverter<long>
    {
        protected override bool TryParse(ReadOnlySpan<byte> text, out long value) => TryInt64(text, long.MinValue, long.MaxValue, out value);
    }

    private sealed class Int16Converter : ScalarConverter<short>
    {
        protected override bool TryParse(ReadOnlySpan<byte> text, out short value)
        {
            bool ok = TryInt64(text, short.MinValue, short.MaxValue, out long l);
            value = (short)l;
            return ok;
        }
    }

    private sealed class SByteConverter : ScalarConverter<sbyte>
    {
        protected override bool TryParse(ReadOnlySpan<byte> text, out sbyte value)
        {
            bool ok = TryInt64(text, sbyte.MinValue, sbyte.MaxValue, out long l);
            value = (sbyte)l;
            return ok;
        }
    }

    private sealed class ByteConverter : ScalarConverter<byte>
    {
        protected override bool TryParse(ReadOnlySpan<byte> text, out byte value)
        {
            bool ok = TryInt64(text, byte.MinValue, byte.MaxValue, out long l);
            value = (byte)l;
            return ok;
        }
    }

    private sealed class UInt16Converter : ScalarConverter<ushort>
    {
        protected override bool TryParse(ReadOnlySpan<byte> text, out ushort value)
        {
            bool ok = TryInt64(text, ushort.MinValue, ushort.MaxValue, out long l);
            value = (ushort)l;
            return ok;
        }
    }

    private sealed class UInt32Converter : ScalarConverter<uint>
    {
        protected override bool TryParse(ReadOnlySpan<byte> text, out uint value)
        {
            bool ok = TryInt64(text, uint.MinValue, uint.MaxValue, out long l);
            value = (uint)l;
            return ok;
        }
    }

    private sealed class UInt64Converter : ScalarConverter<ulong>
    {
        protected override bool TryParse(ReadOnlySpan<byte> text, out ulong value) => YamlScalar.TryParseUInt64(text, out value, YamlSchema.Yaml11);
    }

    private sealed class DoubleConverter : ScalarConverter<double>
    {
        protected override bool TryParse(ReadOnlySpan<byte> text, out double value) => YamlScalar.TryParseDouble(text, out value, YamlSchema.Yaml11);
    }

    private sealed class SingleConverter : ScalarConverter<float>
    {
        protected override bool TryParse(ReadOnlySpan<byte> text, out float value)
        {
            bool ok = YamlScalar.TryParseDouble(text, out double d, YamlSchema.Yaml11);
            value = (float)d;
            return ok;
        }
    }

    private sealed class DecimalConverter : ScalarConverter<decimal>
    {
        protected override bool TryParse(ReadOnlySpan<byte> text, out decimal value) =>
            decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private sealed class DateTimeConverter : ScalarConverter<DateTime>
    {
        protected override bool TryParse(ReadOnlySpan<byte> text, out DateTime value) =>
            DateTime.TryParse(Encoding.UTF8.GetString(text), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
    }

    private sealed class DateTimeOffsetConverter : ScalarConverter<DateTimeOffset>
    {
        protected override bool TryParse(ReadOnlySpan<byte> text, out DateTimeOffset value) =>
            DateTimeOffset.TryParse(Encoding.UTF8.GetString(text), CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    private sealed class GuidConverter : ScalarConverter<Guid>
    {
        protected override bool TryParse(ReadOnlySpan<byte> text, out Guid value) => Utf8Parser.TryParse(text, out value, out int consumed) && consumed == text.Length;
    }
}

/// <summary>Enums are read by name (case-insensitive) or by number, like YamlDotNet; a YAML null gives the default value.</summary>
internal sealed class YamlEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    [UnconditionalSuppressMessage("Trimming", "IL2055", Justification = "Enum types used for deserialization are preserved by their use.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Enum types used for deserialization are preserved by their use.")]
    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(YamlEnumConverter<>).MakeGenericType(typeToConvert))!;
}

internal sealed class YamlEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    public override bool HandleNull => true;

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return default;
        byte[]? rented = null;
        try
        {
            ReadOnlySpan<byte> text = YamlConverters.Text(ref reader, ref rented, typeof(TEnum));
            if (!Enum.TryParse(Encoding.UTF8.GetString(text), ignoreCase: true, out TEnum value)) throw YamlConverters.Bad(text, typeof(TEnum));
            return value;
        }
        finally
        {
            YamlConverters.Return(rented);
        }
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Writing YAML is not supported.");
}
