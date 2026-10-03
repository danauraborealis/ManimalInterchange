using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

const string PrefabKey = "assets/test/prefab.bundle";
const string DependencyKey = "assets/test/dependency.bundle";
const string VanillaDependencyKey = "assets/vanilla/missing.bundle";

try
{
    var options = CommandLine.Parse(args);
    var serverPath = options.ServerPath ?? DefaultServerPath();
    var runtimeRoot = options.RuntimeRoot ?? @"D:\SPT41Dev\SPT_Runtime";
    var wttModsRoot = options.WttModsRoot ?? Path.Combine(runtimeRoot, "user", "mods");

    RequireFile(serverPath, "compiled server assembly");
    RequireDirectory(wttModsRoot, "WTT source mods directory");

    var server = ServerCatalog.Load(serverPath, runtimeRoot);
    var results = new List<string>();

    RunStandaloneSuite(server, wttModsRoot, results);

    if (options.BaselinePath is not null)
    {
        RequireFile(options.BaselinePath, "baseline server assembly");
        RunLegacyStandaloneProbe(options.BaselinePath, runtimeRoot, wttModsRoot, results);
    }

    foreach (var result in results)
    {
        Console.WriteLine(result);
    }

    Console.WriteLine($"PASS {results.Count} standalone-server checks");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL " + error.Message);
    Console.Error.WriteLine(error.ToString());
    return 1;
}

static void RunStandaloneSuite(ServerCatalog server, string wttModsRoot, List<string> results)
{
    Check("catalog load accepts fixture without EscapeFromTarkov_Data", () =>
    {
        using var fixture = Fixture.Create(server.ServerPath, wttModsRoot);
        var catalog = server.LoadCatalog(fixture.AssemblyRoot);
        Require(catalog is not null, "Load returned null.");
        Require(!Directory.Exists(Path.Combine(fixture.Root, "EscapeFromTarkov_Data")), "Fixture unexpectedly created game data.");
    }, results);

    Check("prefab verification accepts absent vanilla dependency", () =>
    {
        using var fixture = Fixture.Create(server.ServerPath, wttModsRoot);
        fixture.WriteManifest(
            new Entry(PrefabKey, new[] { VanillaDependencyKey }));
        fixture.WriteUnityBundle(PrefabKey);

        var catalog = server.LoadCatalog(fixture.AssemblyRoot);
        Require(server.VerifyPrefab(catalog, PrefabKey), "A missing vanilla dependency should be left to the game client.");
        Require(!Directory.Exists(Path.Combine(fixture.Root, "EscapeFromTarkov_Data")), "Standalone verification created a game-data dependency.");
    }, results);

    Check("prefab verification accepts recursive WTT dependency", () =>
    {
        using var fixture = Fixture.Create(server.ServerPath, wttModsRoot);
        fixture.WriteManifest(
            new Entry(PrefabKey, new[] { DependencyKey }),
            new Entry(DependencyKey, Array.Empty<string>()));
        fixture.WriteUnityBundle(PrefabKey);
        fixture.WriteUnityBundle(DependencyKey);

        var catalog = server.LoadCatalog(fixture.AssemblyRoot);
        Require(server.VerifyPrefab(catalog, PrefabKey), "A present, valid WTT dependency should verify.");
    }, results);

    Check("missing declared WTT dependency is rejected", () =>
    {
        using var fixture = Fixture.Create(server.ServerPath, wttModsRoot);
        fixture.WriteManifest(
            new Entry(PrefabKey, new[] { DependencyKey }),
            new Entry(DependencyKey, Array.Empty<string>()));
        fixture.WriteUnityBundle(PrefabKey);

        var catalog = server.LoadCatalog(fixture.AssemblyRoot);
        Require(!server.VerifyPrefab(catalog, PrefabKey), "Missing declared WTT dependency was accepted.");
    }, results);

    Check("corrupt declared WTT dependency is rejected", () =>
    {
        using var fixture = Fixture.Create(server.ServerPath, wttModsRoot);
        fixture.WriteManifest(
            new Entry(PrefabKey, new[] { DependencyKey }),
            new Entry(DependencyKey, Array.Empty<string>()));
        fixture.WriteUnityBundle(PrefabKey);
        fixture.WriteBundle(DependencyKey, new byte[] { 0x6e, 0x6f, 0x74, 0x2d, 0x55, 0x6e, 0x69, 0x74, 0x79 });

        var catalog = server.LoadCatalog(fixture.AssemblyRoot);
        Require(!server.VerifyPrefab(catalog, PrefabKey), "Corrupt declared WTT dependency was accepted.");
    }, results);

    Check("declared WTT dependency hash mismatch is rejected", () =>
    {
        using var fixture = Fixture.Create(server.ServerPath, wttModsRoot);
        fixture.WriteManifest(
            new Entry(PrefabKey, new[] { DependencyKey }),
            new Entry(DependencyKey, Array.Empty<string>(), new string('0', 64)));
        fixture.WriteUnityBundle(PrefabKey);
        fixture.WriteUnityBundle(DependencyKey);

        var catalog = server.LoadCatalog(fixture.AssemblyRoot);
        Require(!server.VerifyPrefab(catalog, PrefabKey), "Hash-mismatched declared WTT dependency was accepted.");
    }, results);

    Check("dependency cycle is rejected", () =>
    {
        using var fixture = Fixture.Create(server.ServerPath, wttModsRoot);
        fixture.WriteManifest(
            new Entry(PrefabKey, new[] { DependencyKey }),
            new Entry(DependencyKey, new[] { PrefabKey }));
        fixture.WriteUnityBundle(PrefabKey);
        fixture.WriteUnityBundle(DependencyKey);

        var catalog = server.LoadCatalog(fixture.AssemblyRoot);
        Require(!server.VerifyPrefab(catalog, PrefabKey), "A cyclic WTT dependency graph was accepted.");
    }, results);

    Check("unsafe manifest key is rejected", () =>
    {
        using var fixture = Fixture.Create(server.ServerPath, wttModsRoot);
        fixture.WriteManifest(new Entry("../escape.bundle", Array.Empty<string>()));
        ExpectInvalidData(() => server.LoadCatalog(fixture.AssemblyRoot), "unsafe manifest key");
    }, results);

    Check("unsafe dependency key is rejected", () =>
    {
        using var fixture = Fixture.Create(server.ServerPath, wttModsRoot);
        fixture.WriteManifest(new Entry(PrefabKey, new[] { "../escape.bundle" }));
        ExpectInvalidData(() => server.LoadCatalog(fixture.AssemblyRoot), "unsafe dependency key");
    }, results);

    Check("missing required WTT files are rejected", () =>
    {
        using (var fixture = Fixture.Create(server.ServerPath, wttModsRoot, copyCommonAssembly: false))
        {
            ExpectInvalidData(() => server.LoadCatalog(fixture.AssemblyRoot), "missing WTT-ServerCommonLib.dll");
        }

        using (var fixture = Fixture.Create(server.ServerPath, wttModsRoot, copyContentAssembly: false))
        {
            ExpectInvalidData(() => server.LoadCatalog(fixture.AssemblyRoot), "missing WTT-ContentBackport.dll");
        }

        using (var fixture = Fixture.Create(server.ServerPath, wttModsRoot, copyManifest: false))
        {
            ExpectInvalidData(() => server.LoadCatalog(fixture.AssemblyRoot), "missing bundles.json");
        }
    }, results);
}

static void RunLegacyStandaloneProbe(string baselinePath, string runtimeRoot, string wttModsRoot, List<string> results)
{
    Check("1.0.7 baseline reproduces game-data rejection", () =>
    {
        using var fixture = Fixture.Create(baselinePath, wttModsRoot);
        var legacy = ServerCatalog.Load(baselinePath, runtimeRoot);
        try
        {
            _ = legacy.LoadCatalog(fixture.AssemblyRoot);
        }
        catch (InvalidDataException error) when (error.Message.Contains("EscapeFromTarkov", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException("The supplied 1.0.7 assembly did not reproduce the expected EscapeFromTarkov_Data rejection.");
    }, results);
}

static void Check(string name, Action action, List<string> results)
{
    try
    {
        action();
        results.Add("PASS " + name);
    }
    catch (Exception error)
    {
        throw new InvalidOperationException(name + ": " + error.Message, error);
    }
}

static void ExpectInvalidData(Action action, string label)
{
    try
    {
        action();
    }
    catch (InvalidDataException)
    {
        return;
    }

    throw new InvalidOperationException("Expected InvalidDataException for " + label + ".");
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void RequireFile(string path, string description)
{
    if (!File.Exists(path))
    {
        throw new FileNotFoundException("Missing " + description + ".", path);
    }
}

static void RequireDirectory(string path, string description)
{
    if (!Directory.Exists(path))
    {
        throw new DirectoryNotFoundException("Missing " + description + ": " + path);
    }
}

static string DefaultServerPath()
{
    var root = Directory.GetParent(AppContext.BaseDirectory)?.Parent?.Parent?.Parent?.Parent?.FullName
        ?? throw new InvalidOperationException("Unable to locate repository root from the verifier output directory.");
    return Path.Combine(root, "interchange-server", "bin", "Release", "net10.0", "ManimalInterchange.Server.dll");
}

sealed record Entry(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("dependencyKeys")] IReadOnlyList<string> Dependencies,
    [property: JsonPropertyName("sha256")] string? Sha256 = null);

sealed class CommandLine
{
    public string? ServerPath { get; private set; }
    public string? BaselinePath { get; private set; }
    public string? RuntimeRoot { get; private set; }
    public string? WttModsRoot { get; private set; }

    public static CommandLine Parse(string[] args)
    {
        var options = new CommandLine();
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            var value = option switch
            {
                "--server" => Next(args, ref index, "--server"),
                "--baseline" => Next(args, ref index, "--baseline"),
                "--runtime" => Next(args, ref index, "--runtime"),
                "--wtt-mods" => Next(args, ref index, "--wtt-mods"),
                _ => throw new ArgumentException("Unknown argument: " + option)
            };

            switch (option)
            {
                case "--server": options.ServerPath = value; break;
                case "--baseline": options.BaselinePath = value; break;
                case "--runtime": options.RuntimeRoot = value; break;
                case "--wtt-mods": options.WttModsRoot = value; break;
            }
        }

        return options;
    }

    private static string Next(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException("Missing value for " + option + ".");
        }

        return Path.GetFullPath(args[index]);
    }
}

sealed class ServerCatalog
{
    private const string CatalogTypeName = "Manimal.Interchange.Server.InterchangeLocationBackport+WttBundleCatalog";

    private readonly MethodInfo _loadMethod;
    private readonly MethodInfo _verifyPrefabMethod;

    private ServerCatalog(string serverPath, Type catalogType)
    {
        ServerPath = serverPath;
        _loadMethod = catalogType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(catalogType.FullName, "Load");
        _verifyPrefabMethod = catalogType.GetMethod("VerifyPrefab", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMethodException(catalogType.FullName, "VerifyPrefab");
    }

    public string ServerPath { get; }

    public static ServerCatalog Load(string serverPath, string runtimeRoot)
    {
        var loadContext = new ServerAssemblyLoadContext(serverPath, runtimeRoot);
        var assembly = loadContext.LoadFromAssemblyPath(serverPath);
        var catalogType = assembly.GetType(CatalogTypeName, throwOnError: true)
            ?? throw new InvalidOperationException("The compiled server assembly does not contain " + CatalogTypeName + ".");
        return new ServerCatalog(serverPath, catalogType);
    }

    public object LoadCatalog(string assemblyRoot)
    {
        try
        {
            return _loadMethod.Invoke(null, new object[] { assemblyRoot })
                ?? throw new InvalidOperationException("WttBundleCatalog.Load returned null.");
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            throw error.InnerException;
        }
    }

    public bool VerifyPrefab(object catalog, string prefabKey)
    {
        try
        {
            return (bool)(_verifyPrefabMethod.Invoke(catalog, new object[] { prefabKey })
                ?? throw new InvalidOperationException("WttBundleCatalog.VerifyPrefab returned null."));
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            throw error.InnerException;
        }
    }
}

sealed class ServerAssemblyLoadContext(string serverPath, string runtimeRoot) : AssemblyLoadContext(isCollectible: true)
{
    private readonly string _serverDirectory = Path.GetDirectoryName(serverPath)
        ?? throw new InvalidOperationException("The server assembly has no parent directory.");

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName.Name))
        {
            return null;
        }

        var candidates = new[]
        {
            Path.Combine(_serverDirectory, assemblyName.Name + ".dll"),
            Path.Combine(runtimeRoot, assemblyName.Name + ".dll")
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return LoadFromAssemblyPath(candidate);
            }
        }

        return null;
    }
}

sealed class Fixture : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private Fixture(string root, string serverPath)
    {
        Root = root;
        ModsRoot = Path.Combine(root, "user", "mods");
        AssemblyRoot = Path.Combine(ModsRoot, "ManimalInterchange");
        ContentRoot = Path.Combine(ModsRoot, "WTT-ContentBackport");
        BundleRoot = Path.Combine(ContentRoot, "bundles");

        Directory.CreateDirectory(AssemblyRoot);
        Directory.CreateDirectory(BundleRoot);
        File.Copy(serverPath, Path.Combine(AssemblyRoot, Path.GetFileName(serverPath)), overwrite: true);
    }

    public string Root { get; }
    public string ModsRoot { get; }
    public string AssemblyRoot { get; }
    public string ContentRoot { get; }
    public string BundleRoot { get; }

    public static Fixture Create(
        string serverPath,
        string wttModsRoot,
        bool copyCommonAssembly = true,
        bool copyContentAssembly = true,
        bool copyManifest = true)
    {
        var fixture = new Fixture(
            Path.Combine(Path.GetTempPath(), "ManimalInterchange-standalone-" + Guid.NewGuid().ToString("N")),
            serverPath);

        var commonSource = Path.Combine(wttModsRoot, "WTT-ServerCommonLib", "WTT-ServerCommonLib.dll");
        var contentSource = Path.Combine(wttModsRoot, "WTT-ContentBackport", "WTT-ContentBackport.dll");
        var manifestSource = Path.Combine(wttModsRoot, "WTT-ContentBackport", "bundles.json");

        if (copyCommonAssembly)
        {
            RequireSourceFile(commonSource, "WTT-ServerCommonLib source assembly");
            var destination = Path.Combine(fixture.ModsRoot, "WTT-ServerCommonLib");
            Directory.CreateDirectory(destination);
            File.Copy(commonSource, Path.Combine(destination, "WTT-ServerCommonLib.dll"));
        }

        if (copyContentAssembly)
        {
            RequireSourceFile(contentSource, "WTT-ContentBackport source assembly");
            File.Copy(contentSource, Path.Combine(fixture.ContentRoot, "WTT-ContentBackport.dll"));
        }

        if (copyManifest)
        {
            RequireSourceFile(manifestSource, "WTT-ContentBackport source manifest");
            File.Copy(manifestSource, Path.Combine(fixture.ContentRoot, "bundles.json"));
        }

        return fixture;
    }

    public void WriteManifest(params Entry[] entries)
    {
        var manifestPath = Path.Combine(ContentRoot, "bundles.json");
        var payload = new { manifest = entries };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(payload, JsonOptions));
    }

    public void WriteUnityBundle(string key)
    {
        WriteBundle(key, CreateUnityBundleBytes(key));
    }

    public void WriteBundle(string key, byte[] content)
    {
        var path = Path.Combine(BundleRoot, key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? BundleRoot);
        File.WriteAllBytes(path, content);
    }

    public void Dispose()
    {
        try
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var fixtureRoot = Path.GetFullPath(Root);
            RequireSafeCleanup(fixtureRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase), "Refusing to clean fixture outside the temporary directory.");
            RequireSafeCleanup(Path.GetFileName(fixtureRoot).StartsWith("ManimalInterchange-standalone-", StringComparison.Ordinal), "Refusing to clean an unexpected fixture directory.");
            if (Directory.Exists(fixtureRoot))
            {
                Directory.Delete(fixtureRoot, recursive: true);
            }
        }
        catch (IOException error)
        {
            Console.Error.WriteLine("WARNING fixture cleanup failed: " + Root + ": " + error.Message);
        }
    }

    private static byte[] CreateUnityBundleBytes(string key)
    {
        var bytes = new byte[64];
        "UnityFS\0"u8.CopyTo(bytes);
        var suffix = System.Text.Encoding.UTF8.GetBytes(key);
        suffix.AsSpan(0, Math.Min(suffix.Length, bytes.Length - 8)).CopyTo(bytes.AsSpan(8));
        return bytes;
    }

    private static void RequireSourceFile(string path, string description)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Missing " + description + ".", path);
        }
    }

    private static void RequireSafeCleanup(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
