using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace System.Text.Yaml.Serialization;

/// <summary>
/// Deserializes YAML into .NET objects the way <see cref="JsonSerializer"/> does for JSON: type metadata is built once and cached
/// (reflection, or source generation through <see cref="JsonTypeInfo{T}"/>), the YAML is rendered as UTF-8 JSON into a pooled
/// buffer and bound to the target type without intermediate object graphs. Reading only: there is no YAML writer.
/// </summary>
public static class YamlSerializer
{
    private const string ReflectionMessage = "Reflection-based serialization may require types that cannot be statically analyzed. Use the overload that takes a JsonTypeInfo<T> (System.Text.Json source generation).";

    // ---------------------------------------------------------------------------------------------
    // Reflection-based
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Deserializes one YAML document. An empty document (or a comment-only one, or <c>null</c>/<c>~</c>) gives <c>default</c>.
    /// </summary>
    /// <exception cref="YamlException">Malformed YAML, or a value that cannot be converted to the target type.</exception>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static T? Deserialize<T>(string yaml, YamlSerializerOptions? options = null)
    {
        options ??= YamlSerializerOptions.Default;
        JsonSerializerOptions json = options.JsonOptions;
        using var holder = new DocumentHolder(yaml, options);
        YamlDocument doc = holder.Document;
        return Bind<T>(doc.RootElement, options, json, null);
    }

    /// <inheritdoc cref="Deserialize{T}(string, YamlSerializerOptions)"/>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static T? Deserialize<T>(ReadOnlyMemory<byte> utf8Yaml, YamlSerializerOptions? options = null)
    {
        options ??= YamlSerializerOptions.Default;
        JsonSerializerOptions json = options.JsonOptions;
        using var holder = new DocumentHolder(utf8Yaml, options);
        YamlDocument doc = holder.Document;
        return Bind<T>(doc.RootElement, options, json, null);
    }

    /// <inheritdoc cref="Deserialize{T}(string, YamlSerializerOptions)"/>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static T? Deserialize<T>(Stream stream, YamlSerializerOptions? options = null)
    {
        options ??= YamlSerializerOptions.Default;
        JsonSerializerOptions json = options.JsonOptions;
        using YamlDocument doc = YamlDocument.Parse(stream, options.DocumentOptions);
        return Bind<T>(doc.RootElement, options, json, null);
    }

    /// <summary>Deserializes an already parsed node.</summary>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static T? Deserialize<T>(YamlElement element, YamlSerializerOptions? options = null)
    {
        options ??= YamlSerializerOptions.Default;
        return Bind<T>(element, options, options.JsonOptions, null);
    }

    /// <summary>Deserializes one YAML document into <paramref name="returnType"/>.</summary>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static object? Deserialize(string yaml, Type returnType, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(returnType);
        options ??= YamlSerializerOptions.Default;
        JsonSerializerOptions json = options.JsonOptions;
        using var holder = new DocumentHolder(yaml, options);
        YamlDocument doc = holder.Document;
        return BindObject(doc.RootElement, returnType, options, json);
    }

    // ---------------------------------------------------------------------------------------------
    // Source-generated metadata
    // ---------------------------------------------------------------------------------------------

    /// <summary>Deserializes one YAML document using source-generated metadata (trim and AOT friendly).</summary>
    public static T? Deserialize<T>(string yaml, JsonTypeInfo<T> typeInfo, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        options ??= YamlSerializerOptions.Default;
        _ = options.JsonOptions;
        using var holder = new DocumentHolder(yaml, options);
        YamlDocument doc = holder.Document;
        return Bind(doc.RootElement, options, typeInfo);
    }

    /// <inheritdoc cref="Deserialize{T}(string, JsonTypeInfo{T}, YamlSerializerOptions)"/>
    public static T? Deserialize<T>(ReadOnlyMemory<byte> utf8Yaml, JsonTypeInfo<T> typeInfo, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        options ??= YamlSerializerOptions.Default;
        _ = options.JsonOptions;
        using var holder = new DocumentHolder(utf8Yaml, options);
        YamlDocument doc = holder.Document;
        return Bind(doc.RootElement, options, typeInfo);
    }

    // ---------------------------------------------------------------------------------------------
    // Core
    // ---------------------------------------------------------------------------------------------

    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    private static T? Bind<T>(YamlElement root, YamlSerializerOptions options, JsonSerializerOptions json, object? unused)
    {
        if (root.IsNull) return default;
        using var buffer = new PooledBufferWriter();
        Render(root, options, buffer);
        try
        {
            return JsonSerializer.Deserialize<T>(buffer.WrittenSpan, json);
        }
        catch (JsonException ex)
        {
            throw Wrap(ex, root);
        }
    }

    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    private static object? BindObject(YamlElement root, Type returnType, YamlSerializerOptions options, JsonSerializerOptions json)
    {
        if (root.IsNull) return returnType.IsValueType && Nullable.GetUnderlyingType(returnType) is null ? Activator.CreateInstance(returnType) : null;
        using var buffer = new PooledBufferWriter();
        Render(root, options, buffer);
        try
        {
            return JsonSerializer.Deserialize(buffer.WrittenSpan, returnType, json);
        }
        catch (JsonException ex)
        {
            throw Wrap(ex, root);
        }
    }

    private static T? Bind<T>(YamlElement root, YamlSerializerOptions options, JsonTypeInfo<T> typeInfo)
    {
        if (root.IsNull) return default;
        using var buffer = new PooledBufferWriter();
        Render(root, options, buffer);
        try
        {
            return JsonSerializer.Deserialize(buffer.WrittenSpan, typeInfo);
        }
        catch (JsonException ex)
        {
            throw Wrap(ex, root);
        }
    }

    private static readonly JsonWriterOptions s_writerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // accents stay UTF-8: no \uXXXX expansion; the JSON never reaches a browser
        SkipValidation = true,
        Indented = false,
    };

    [ThreadStatic]
    private static Utf8JsonWriter? t_writer;

    private static void Render(YamlElement root, YamlSerializerOptions options, PooledBufferWriter buffer)
    {
        Utf8JsonWriter? writer = t_writer;
        if (writer is null)
        {
            writer = t_writer = new Utf8JsonWriter(buffer, s_writerOptions);
        }
        else
        {
            writer.Reset(buffer);
        }

        try
        {
            long budget = options.MaxNodes;
            root.WriteAsJsonStrings(writer, ref budget, options.MappedTags);
            writer.Flush();
        }
        finally
        {
            writer.Reset(); // releases the reference to the buffer
        }
    }

    /// <summary>Turns a binding error into a <see cref="YamlException"/> carrying the member path and the YAML line and column.</summary>
    private static YamlException Wrap(JsonException ex, YamlElement root)
    {
        string message = ex.Message;
        int pathMarker = message.IndexOf(" Path: ", StringComparison.Ordinal);
        if (pathMarker >= 0) message = message.Substring(0, pathMarker);
        message = message.Replace("The JSON value", "The YAML value", StringComparison.Ordinal).TrimEnd('.', ' ');
        if (ex.InnerException is { } inner && !message.Contains(inner.Message, StringComparison.Ordinal)) message += ": " + inner.Message.TrimEnd('.');

        string? path = ex.Path;
        string reason = path is { Length: > 1 } ? $"{message} (path {path})" : message;

        int offset = path is null ? -1 : root.FindOffset(path);
        return offset >= 0 ? YamlException.At(root.Document.DataSpan, offset, reason) : new YamlException(reason);
    }


    /// <summary>Owns a parsed document, or borrows the shared one from the <see cref="YamlDocumentCache"/> (then disposing does nothing).</summary>
    private readonly struct DocumentHolder : IDisposable
    {
        private readonly bool _owned;

        public DocumentHolder(string yaml, YamlSerializerOptions options)
        {
            YamlDocumentCache? cache = options.DocumentCache;
            if (cache is null)
            {
                Document = YamlDocument.Parse(yaml, options.DocumentOptions);
                _owned = true;
                return;
            }

            byte[] rented = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(yaml)); // exact size: the worst case (3 bytes per char) would exceed the pool's largest bucket
            try
            {
                int n = Encoding.UTF8.GetBytes(yaml, rented);
                Document = cache.GetOrParse(rented.AsMemory(0, n), options.DocumentOptions);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public DocumentHolder(ReadOnlyMemory<byte> utf8Yaml, YamlSerializerOptions options)
        {
            YamlDocumentCache? cache = options.DocumentCache;
            if (cache is null)
            {
                Document = YamlDocument.Parse(utf8Yaml, options.DocumentOptions);
                _owned = true;
            }
            else
            {
                Document = cache.GetOrParse(utf8Yaml, options.DocumentOptions);
            }
        }

        public YamlDocument Document { get; }

        public void Dispose()
        {
            if (_owned) Document.Dispose();
        }
    }

    /// <summary>A growable <see cref="IBufferWriter{T}"/> over <see cref="ArrayPool{T}"/> buffers.</summary>
    private sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        private int _index;

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _index);

        public void Advance(int count) => _index += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Grow(sizeHint);
            return _buffer.AsMemory(_index);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Grow(sizeHint);
            return _buffer.AsSpan(_index);
        }

        private void Grow(int sizeHint)
        {
            if (sizeHint < 1) sizeHint = 256;
            if (_buffer.Length - _index >= sizeHint) return;

            int newSize = Math.Max(_buffer.Length * 2, _index + sizeHint);
            byte[] bigger = ArrayPool<byte>.Shared.Rent(newSize);
            _buffer.AsSpan(0, _index).CopyTo(bigger);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = bigger;
        }

        public void Dispose()
        {
            byte[] b = _buffer;
            _buffer = [];
            if (b.Length > 0) ArrayPool<byte>.Shared.Return(b);
        }
    }
}
