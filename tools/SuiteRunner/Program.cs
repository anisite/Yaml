using System.Text;
using System.Text.Yaml;
using System.Text.Yaml.Tests;

// usage: SuiteRunner <suite-dir> [-v] [id-prefix]   |   SuiteRunner fuzz <suite-dir> <mutations> <seed>
if (args.Length > 0 && args[0] == "fuzz")
{
    return Fuzz.Run(args[1], int.Parse(args[2]), int.Parse(args[3]));
}

string root = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "suite");
bool verbose = args.Contains("-v");
string? filter = args.Skip(1).FirstOrDefault(a => a != "-v");

int pass = 0, fail = 0;
var failures = new List<string>();
foreach (string dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
{
    var cases = new List<string>();
    if (File.Exists(Path.Combine(dir, "in.yaml"))) cases.Add(dir);
    else cases.AddRange(Directory.EnumerateDirectories(dir).Where(d => File.Exists(Path.Combine(d, "in.yaml"))).OrderBy(d => d, StringComparer.Ordinal));

    foreach (string c in cases)
    {
        string id = Path.GetRelativePath(root, c).Replace('\\', '/');
        if (filter is not null && !id.StartsWith(filter, StringComparison.Ordinal)) continue;
        byte[] yaml = File.ReadAllBytes(Path.Combine(c, "in.yaml"));
        bool expectError = File.Exists(Path.Combine(c, "error"));
        string name = File.Exists(Path.Combine(dir, "===")) ? File.ReadAllText(Path.Combine(dir, "===")).Trim() : "";
        string? actual = null; string? err = null;
        try { actual = EventDump.Dump(yaml); }
        catch (YamlException e) { err = e.Message; }
        catch (Exception e) { err = "CRASH " + e.GetType().Name + ": " + e.Message; }

        bool ok;
        string detail = "";
        if (expectError)
        {
            ok = err is not null && !err.StartsWith("CRASH");
            if (!ok) detail = err is null ? "expected error but parsed OK:\n" + actual : err;
        }
        else
        {
            string expected = File.ReadAllText(Path.Combine(c, "test.event")).Replace("\r\n", "\n");
            ok = actual == expected;
            if (!ok) detail = err is not null ? "unexpected error: " + err : "expected:\n" + expected + "got:\n" + actual;
        }
        if (ok) pass++;
        else
        {
            fail++;
            failures.Add($"{id} ({name}){(expectError ? " [error expected]" : "")}");
            if (verbose)
            {
                Console.WriteLine($"=== {id} {name}");
                Console.WriteLine(Encoding.UTF8.GetString(yaml).Replace("\t", "→").Replace("\r", "␍"));
                Console.WriteLine("---");
                Console.WriteLine(detail);
            }
        }
    }
}
Console.WriteLine($"pass={pass} fail={fail}");
foreach (string f in failures) Console.WriteLine("FAIL " + f);
return fail == 0 ? 0 : 1;
