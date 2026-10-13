using System.Buffers;
using System.Text.Unicode;

namespace YamlD;

/// <summary>
/// Input handling shared by the reader and the DOM: YAML 1.2.2 §5.2 encoding detection
/// (UTF-8, UTF-16 and UTF-32, either endianness) and up-front validation of the stream.
/// </summary>
public static class YamlEncoding
{
    // c-printable minus everything that is a valid ASCII control char we must reject.
    // Allowed ASCII controls: TAB, LF, CR. Rejected: 00-08, 0B-0C, 0E-1F, 7F.
    private static readonly SearchValues<byte> s_badAscii = SearchValues.Create(
        [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x0B, 0x0C,
         0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
         0x7F]);

    // Lead bytes that may start a non-printable sequence: C2 (C1 controls) and EF (U+FFFE/U+FFFF).
    private static readonly SearchValues<byte> s_suspectLead = SearchValues.Create([0xC2, 0xEF]);

    /// <summary>
    /// Returns the offset of the first byte that is not valid YAML character data (ill-formed UTF-8 or a
    /// character outside <c>c-printable</c>), or -1 if the whole span is valid.
    /// </summary>
    public static int FindInvalid(ReadOnlySpan<byte> data)
    {
        // Fast path: pure-ASCII or well-formed UTF-8 is verified with the vectorized runtime routine.
        if (!Utf8.IsValid(data))
        {
            return FirstIllFormed(data);
        }

        int bad = data.IndexOfAny(s_badAscii);
        int limit = bad >= 0 ? bad : data.Length;

        int i = 0;
        while (i < limit)
        {
            int rel = data.Slice(i, limit - i).IndexOfAny(s_suspectLead);
            if (rel < 0) break;
            i += rel;
            byte b1 = i + 1 < data.Length ? data[i + 1] : (byte)0;
            if (data[i] == 0xC2)
            {
                // U+0080..U+009F except U+0085 (NEL)
                if (b1 >= 0x80 && b1 <= 0x9F && b1 != 0x85) return i;
                i += 2;
            }
            else
            {
                // EF BF BE / EF BF BF => U+FFFE / U+FFFF
                if (b1 == 0xBF && i + 2 < data.Length && (data[i + 2] == 0xBE || data[i + 2] == 0xBF)) return i;
                i += 3;
            }
        }

        return bad;
    }

    private static int FirstIllFormed(ReadOnlySpan<byte> data)
    {
        // Slow path, only executed on error: locate the first invalid sequence.
        int offset = 0;
        while (offset < data.Length)
        {
            OperationStatus status = Rune.DecodeFromUtf8(data.Slice(offset), out _, out int consumed);
            if (status != OperationStatus.Done) return offset;
            offset += consumed;
        }
        return 0;
    }

    /// <summary>
    /// Converts any supported encoding to UTF-8 if needed. UTF-8 input (with or without BOM) is returned
    /// as-is (<paramref name="owned"/> is <c>null</c>).
    /// </summary>
    /// <param name="raw">Raw bytes of the stream.</param>
    /// <param name="owned">When non-null the transcoded UTF-8 buffer; the returned span points into it.</param>
    public static ReadOnlySpan<byte> ToUtf8(ReadOnlySpan<byte> raw, out byte[]? owned)
    {
        owned = null;
        Encoding? enc = Detect(raw, out int bomLength);
        if (enc is null)
        {
            return raw; // UTF-8 (BOM, if any, is stripped by the reader)
        }

        string text;
        try
        {
            text = enc.GetString(raw.Slice(bomLength));
        }
        catch (DecoderFallbackException)
        {
            throw new YamlException($"The stream is not valid {enc.EncodingName}");
        }
        owned = Encoding.UTF8.GetBytes(text);
        return owned;
    }

    /// <summary>Detects UTF-16/32 (BOM or null-byte pattern). Returns null for UTF-8.</summary>
    internal static Encoding? Detect(ReadOnlySpan<byte> d, out int bomLength)
    {
        bomLength = 0;
        if (d.Length >= 4)
        {
            if (d[0] == 0x00 && d[1] == 0x00 && d[2] == 0xFE && d[3] == 0xFF) { bomLength = 4; return new UTF32Encoding(true, false, true); }
            if (d[0] == 0xFF && d[1] == 0xFE && d[2] == 0x00 && d[3] == 0x00) { bomLength = 4; return new UTF32Encoding(false, false, true); }
        }
        if (d.Length >= 2)
        {
            if (d[0] == 0xFE && d[1] == 0xFF) { bomLength = 2; return new UnicodeEncoding(true, false, true); }
            if (d[0] == 0xFF && d[1] == 0xFE) { bomLength = 2; return new UnicodeEncoding(false, false, true); }
        }
        if (d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF)
        {
            return null;
        }
        // No BOM: YAML 1.2.2 §5.2 null-byte patterns (first character is ASCII).
        if (d.Length >= 4)
        {
            if (d[0] == 0 && d[1] == 0 && d[2] == 0 && d[3] != 0) return new UTF32Encoding(true, false, true);
            if (d[0] != 0 && d[1] == 0 && d[2] == 0 && d[3] == 0) return new UTF32Encoding(false, false, true);
        }
        if (d.Length >= 2)
        {
            if (d[0] == 0 && d[1] != 0) return new UnicodeEncoding(true, false, true);
            if (d[0] != 0 && d[1] == 0) return new UnicodeEncoding(false, false, true);
        }
        return null;
    }
}
