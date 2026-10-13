using System.Text.Json;
using System.Text.Json.Nodes;

namespace YamlD.Tests;

internal static class Check
{
    /// <summary>Asserts that <paramref name="action"/> throws exactly <typeparamref name="T"/> (or a subclass).</summary>
    public static T Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T e)
        {
            return e;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name} but nothing was thrown.");
    }
}

/// <summary>
/// Runs the official yaml-test-suite (https://github.com/yaml/yaml-test-suite, tag data-2022-01-17):
/// the event stream must match <c>test.event</c>, invalid inputs must be rejected, and the DOM converted to JSON
/// must match <c>in.json</c> for valid inputs that have one.
/// </summary>
public class SuiteTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "suite");

    public static IEnumerable<string> Cases()
    {
        foreach (string dir in Directory.EnumerateDirectories(Root).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (File.Exists(Path.Combine(dir, "in.yaml")))
            {
                yield return Path.GetFileName(dir);
            }
            else
            {
                foreach (string sub in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
                {
                    if (File.Exists(Path.Combine(sub, "in.yaml")))
                    {
                        yield return Path.GetFileName(dir) + "/" + Path.GetFileName(sub);
                    }
                }
            }
        }
    }

    [Test]
    public async Task SuiteIsPresent()
    {
        await Assert.That(Cases().Count()).IsGreaterThanOrEqualTo(400);
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Events(string id)
    {
        string dir = Path.Combine(Root, id);
        byte[] yaml = File.ReadAllBytes(Path.Combine(dir, "in.yaml"));

        if (File.Exists(Path.Combine(dir, "error")))
        {
            Check.Throws<YamlException>(() => EventDump.Dump(yaml));
            return;
        }

        string expected = File.ReadAllText(Path.Combine(dir, "test.event")).Replace("\r\n", "\n");
        await Assert.That(EventDump.Dump(yaml)).IsEqualTo(expected);
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task DocumentMatchesJson(string id)
    {
        string dir = Path.Combine(Root, id);
        string jsonPath = Path.Combine(dir, "in.json");
        if (File.Exists(Path.Combine(dir, "error")) || !File.Exists(jsonPath)) return;

        byte[] yaml = File.ReadAllBytes(Path.Combine(dir, "in.yaml"));
        List<JsonNode?> expected = ReadAllJson(File.ReadAllBytes(jsonPath));

        List<YamlDocument> docs = YamlDocument.ParseAll(yaml);
        try
        {
            await Assert.That(docs.Count).IsEqualTo(expected.Count);
            for (int i = 0; i < docs.Count; i++)
            {
                JsonNode? actual = JsonNode.Parse(docs[i].RootElement.ToJsonString());
                bool same = JsonNode.DeepEquals(expected[i], actual);
                if (!same)
                {
                    throw new InvalidOperationException(
                        $"Document {i} differs.\nExpected: {expected[i]?.ToJsonString()}\nActual:   {actual?.ToJsonString()}");
                }
            }
        }
        finally
        {
            foreach (YamlDocument d in docs) d.Dispose();
        }
    }

    private static List<JsonNode?> ReadAllJson(byte[] bytes)
    {
        var result = new List<JsonNode?>();
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowMultipleValues = true });
        while (reader.Read())
        {
            result.Add(JsonNode.Parse(ref reader));
        }
        return result;
    }
}
