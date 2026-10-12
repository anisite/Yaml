using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace System.Text.Yaml;

/// <summary>Thrown when the input is not a well-formed YAML 1.2.2 stream.</summary>
public sealed class YamlException : FormatException
{
    /// <summary>1-based line of the error, or 0 if unknown.</summary>
    public long LineNumber { get; }

    /// <summary>1-based column (in bytes) of the error, or 0 if unknown.</summary>
    public long ColumnNumber { get; }

    /// <summary>Zero-based byte offset in the UTF-8 input.</summary>
    public long ByteOffset { get; }

    /// <summary>The error message without the position suffix.</summary>
    public string Reason { get; }

    public YamlException(string message) : base(message)
    {
        Reason = message;
    }

    public YamlException(string reason, long byteOffset, long line, long column)
        : base(line > 0 ? $"{reason} (line {line}, column {column})" : reason)
    {
        Reason = reason;
        ByteOffset = byteOffset;
        LineNumber = line;
        ColumnNumber = column;
    }

    internal static YamlException At(ReadOnlySpan<byte> data, int offset, string reason)
    {
        if (offset < 0) offset = 0;
        if (offset > data.Length) offset = data.Length;
        long line = 1;
        int lineStart = 0;
        for (int i = 0; i < offset; i++)
        {
            byte b = data[i];
            if (b == (byte)'\n' || (b == (byte)'\r' && !(i + 1 < data.Length && data[i + 1] == (byte)'\n')))
            {
                line++;
                lineStart = i + 1;
            }
        }
        return new YamlException(reason, offset, line, offset - lineStart + 1);
    }
}

internal static class ThrowHelper
{
    [DoesNotReturn]
    public static void Throw(ReadOnlySpan<byte> data, int offset, string reason) =>
        throw YamlException.At(data, offset, reason);

    [DoesNotReturn]
    public static void ThrowInvalidOperation(string message) => throw new InvalidOperationException(message);

    [DoesNotReturn]
    public static void ThrowFormat(string message) => throw new FormatException(message);

    [DoesNotReturn]
    public static void ThrowArgumentOutOfRange(string name) => throw new ArgumentOutOfRangeException(name);

    [DoesNotReturn]
    public static void ThrowObjectDisposed() => throw new ObjectDisposedException("YamlDocument");

    [DoesNotReturn]
    public static void ThrowKeyNotFound(string key) => throw new KeyNotFoundException($"Mapping key '{key}' was not found.");
}
