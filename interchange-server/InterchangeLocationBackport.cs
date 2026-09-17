using System.Reflection;
using System.Text.Json;
using JetBrains.Annotations;
using Manimal.Interchange.Shared;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using TemplateItem = SPTarkov.Server.Core.Models.Eft.Common.Tables.TemplateItem;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Json;

namespace Manimal.Interchange.Server;

/// <summary>
/// Coordinates the in-memory Interchange replacement.  Preparation reads and
/// validates all payloads against a combined template view.  Only after that
/// succeeds are the 16 item records, locale labels, and location assigned as
/// one startup transaction.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 4), UsedImplicitly]
public sealed class InterchangeLocationBackport(
    LocationTable locations,
    TemplateTable templates,
    LocaleTable locales,
    JsonUtil json) : IOnLoad
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };

    private const int MinimumLocaleKeys = 67;
    private RegistrationReceipt? _registration;

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RollbackRegistration();
        InterchangeServerState.BeginStartup();

        var root = Path.GetDirectoryName(typeof(InterchangeLocationBackport).Assembly.Location)!;
        var manifestPath = Path.Combine(root, ManifestRules.FileName);

        if (!File.Exists(manifestPath))
        {
            InterchangeServerState.MarkInactive("", "", "", "Interchange content manifest is not installed.");
            return Task.CompletedTask;
        }

        ContentManifest? manifest = null;
        var manifestHash = "";

        try
        {
            manifest = JsonSerializer.Deserialize<ContentManifest>(File.ReadAllText(manifestPath), JsonOptions)
                ?? throw new InvalidDataException("Null Interchange content manifest");

            ManifestRules.Validate(manifest);
            manifestHash = ManifestRules.Hash(manifestPath);

            if (manifest.Mode == "probe")
            {
                // Probe mode deliberately performs no gameplay registration.
                InterchangeServerState.MarkProbe(
                    manifest.ContentId,
                    manifestHash,
                    File.Exists(Path.Combine(root, "allow-probe")));
                return Task.CompletedTask;
            }

            if (manifest.Mode is not ("test" or "rework"))
            {
                InterchangeServerState.MarkInactive(
                    manifest.ContentId,
                    manifest.Mode,
                    manifestHash,
                    "Interchange content mode does not activate server data.");
                return Task.CompletedTask;
            }

            if (!InterchangeServerState.Allows(manifest, root))
            {
                InterchangeServerState.MarkInactive(
                    manifest.ContentId,
                    manifest.Mode,
                    manifestHash,
                    manifest.Mode == "test"
                        ? "Test content requires the allow-fullmap-test sentinel."
                        : "Rework content must be marked ready in the manifest.");
                return Task.CompletedTask;
            }

            cancellationToken.ThrowIfCancellationRequested();
            VerifyServerPayloads(root, manifest);

            var resourceCatalog = WttBundleCatalog.Load(root);
            var preparedItems = PrepareItems(root, manifest, resourceCatalog, templates.Items, json);
            var preparedLocales = PrepareLocales(root, manifest, json);

            // The location reader validates all loot _tpl references against
            // this combined view.  The live template table remains untouched
            // until the transaction enters its commit phase.
            var combinedTemplates = new Dictionary<MongoId, TemplateItem>(templates.Items);
            foreach (var pair in preparedItems.Templates)
            {
                if (!combinedTemplates.TryAdd(pair.Key, pair.Value))
                {
                    throw new InvalidDataException("Prepared Interchange item already exists in the combined target table: " + pair.Key);
                }
            }

            var baseline = Manimal.MapBackport.LegacyLootCompatibility.ReadBaseline(json, "interchange");
            var replacement = InterchangeLocationData.Read(root, manifest, json, combinedTemplates, baseline);
            cancellationToken.ThrowIfCancellationRequested();

            _registration = Commit(
                locations.Interchange ?? throw new InvalidDataException("Target Interchange location is missing."),
                templates.Items,
                locales,
                preparedItems,
                preparedLocales,
                replacement,
                cancellationToken);

            InterchangeServerState.MarkApplied(manifest, manifestHash);
            return Task.CompletedTask;
        }
        catch (Exception error)
        {
            RollbackRegistration();
            InterchangeServerState.MarkFailure(
                manifest?.ContentId ?? "",
                manifest?.Mode ?? "",
                manifestHash,
                error);
            throw;
        }
    }

    /// <summary>
    /// Explicitly rolls back a successful startup registration.  This gives a
    /// host or a test fixture a deterministic unload/deactivation boundary;
    /// the normal server path never calls it during a raid.
    /// </summary>
    public void RollbackRegistration()
    {
        if (_registration is not null)
        {
            _registration.Rollback();
            _registration = null;
        }

        InterchangeServerState.Clear();
    }

    private static void VerifyServerPayloads(string root, ContentManifest manifest)
    {
        foreach (var file in manifest.ServerFiles)
        {
            ManifestRules.VerifyFile(root, file);
        }

        foreach (var file in manifest.ItemFiles)
        {
            ManifestRules.VerifyFile(root, file);
        }
    }

    private InterchangeItemCompatibility.PreparedItemSet PrepareItems(
        string root,
        ContentManifest manifest,
        WttBundleCatalog resources,
        Dictionary<MongoId, TemplateItem> liveTemplates,
        JsonUtil json)
    {
        var declaration = FindItemFile(manifest, InterchangeItemCompatibility.PreparedTemplateFileName);
        var prepared = InterchangeItemCompatibility.ReadPrepared(json, root, declaration);
        return InterchangeItemCompatibility.Prepare(
            liveTemplates,
            prepared,
            resources.VerifyPrefab,
            allowExistingRecords: false);
    }

    private Dictionary<string, string> PrepareLocales(
        string root,
        ContentManifest manifest,
        JsonUtil json)
    {
        var declaration = FindItemFile(manifest, InterchangeItemCompatibility.PreparedLocaleFileName);
        var prepared = InterchangeItemCompatibility.ReadPreparedLocales(json, root, declaration);
        if (prepared.Count < MinimumLocaleKeys)
        {
            throw new InvalidDataException(
                $"Interchange locale fragment has {prepared.Count} keys; expected at least {MinimumLocaleKeys}.");
        }

        foreach (var pair in prepared)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
            {
                throw new InvalidDataException("Interchange locale fragment contains an empty key or null value.");
            }
        }

        return prepared;
    }

    private static PayloadFile FindItemFile(ContentManifest manifest, string fileName)
    {
        PayloadFile? found = null;
        foreach (var file in manifest.ItemFiles)
        {
            if (!string.Equals(Path.GetFileName(file.Path), fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found is not null)
            {
                throw new InvalidDataException("Duplicate Interchange ItemFiles declaration: " + fileName);
            }

            found = file;
        }

        return found ?? throw new InvalidDataException("Missing Interchange ItemFiles declaration: " + fileName);
    }

    private static RegistrationReceipt Commit(
        Location targetLocation,
        Dictionary<MongoId, TemplateItem> liveTemplates,
        LocaleTable locales,
        InterchangeItemCompatibility.PreparedItemSet preparedItems,
        IReadOnlyDictionary<string, string> preparedLocales,
        Location replacement,
        CancellationToken cancellationToken)
    {
        InterchangeItemCompatibility.CommitReceipt? itemReceipt = null;
        LocaleCommitReceipt? localeReceipt = null;
        Location? locationSnapshot = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            itemReceipt = InterchangeItemCompatibility.Commit(liveTemplates, preparedItems);

            cancellationToken.ThrowIfCancellationRequested();
            localeReceipt = ApplyLocales(locales, preparedLocales);

            cancellationToken.ThrowIfCancellationRequested();
            locationSnapshot = InterchangeLocationData.Snapshot(targetLocation);
            InterchangeLocationData.Apply(targetLocation, replacement);

            return new RegistrationReceipt(
                targetLocation,
                locationSnapshot,
                itemReceipt,
                localeReceipt);
        }
        catch
        {
            if (locationSnapshot is not null)
            {
                InterchangeLocationData.Restore(targetLocation, locationSnapshot);
            }

            localeReceipt?.Rollback();
            itemReceipt?.Rollback();
            throw;
        }
    }

    private static LocaleCommitReceipt ApplyLocales(
        LocaleTable locales,
        IReadOnlyDictionary<string, string> additions)
    {
        if (locales.Global is null || locales.Global.Count == 0)
        {
            throw new InvalidDataException("Target locale table has no global language dictionaries.");
        }

        var snapshots = new List<LocaleSnapshot>();
        foreach (var pair in locales.Global)
        {
            var original = pair.Value
                ?? throw new InvalidDataException("Null target language dictionary: " + pair.Key);
            var replacement = new LazyLoad<GlobalLocaleDictionary>(() =>
            {
                var source = original.Value
                    ?? throw new InvalidDataException("Target language resolved to null: " + pair.Key);
                var result = new GlobalLocaleDictionary();
                foreach (var entry in source) result.Add(entry.Key, entry.Value);
                foreach (var entry in additions) result.TryAdd(entry.Key, entry.Value);
                return result;
            }, false);
            // Resolve once before commit to reject invalid inputs. Each future
            // load copies the original, including its current transformer chain.
            _ = replacement.Value;
            snapshots.Add(new LocaleSnapshot(pair.Key, original, replacement));
        }
        var receipt = new LocaleCommitReceipt(locales, snapshots);
        try
        {
            foreach (var snapshot in snapshots) locales.Global[snapshot.Language] = snapshot.Replacement;
            return receipt;
        }
        catch
        {
            receipt.Rollback();
            throw;
        }
    }

    private sealed class RegistrationReceipt(
        Location targetLocation,
        Location locationSnapshot,
        InterchangeItemCompatibility.CommitReceipt itemReceipt,
        LocaleCommitReceipt localeReceipt)
    {
        private bool _rolledBack;

        public void Rollback()
        {
            if (_rolledBack)
            {
                return;
            }

            InterchangeLocationData.Restore(targetLocation, locationSnapshot);
            localeReceipt.Rollback();
            itemReceipt.Rollback();
            _rolledBack = true;
        }
    }

    private sealed class LocaleCommitReceipt(LocaleTable locales, IReadOnlyList<LocaleSnapshot> snapshots)
    {
        private bool _rolledBack;

        public void Rollback()
        {
            if (_rolledBack) return;
            foreach (var snapshot in snapshots) locales.Global[snapshot.Language] = snapshot.Original;
            _rolledBack = true;
        }
    }

    private sealed record LocaleSnapshot(
        string Language,
        LazyLoad<GlobalLocaleDictionary> Original,
        LazyLoad<GlobalLocaleDictionary> Replacement);

    private sealed class WttBundleCatalog
    {
        private readonly string _bundleRoot;
        private readonly string _streamingRoot;
        private readonly Dictionary<string, BundleEntry> _entries;
        private readonly Dictionary<string, bool> _prefabVerification = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _hashVerification = new(StringComparer.OrdinalIgnoreCase);

        private WttBundleCatalog(
            string bundleRoot,
            string streamingRoot,
            Dictionary<string, BundleEntry> entries)
        {
            _bundleRoot = bundleRoot;
            _streamingRoot = streamingRoot;
            _entries = entries;
        }

        public static WttBundleCatalog Load(string assemblyRoot)
        {
            var modsRoot = Directory.GetParent(assemblyRoot)?.FullName
                ?? throw new InvalidDataException("Unable to resolve the SPT mods directory.");
            var userRoot = Directory.GetParent(modsRoot)?.FullName
                ?? throw new InvalidDataException("Unable to resolve the SPT user directory.");
            var runtimeRoot = Directory.GetParent(userRoot)?.FullName
                ?? throw new InvalidDataException("Unable to resolve the SPT runtime directory.");
            var installRoot = Directory.GetParent(runtimeRoot)?.FullName
                ?? throw new InvalidDataException("Unable to resolve the SPT install directory.");

            var commonRoot = Path.Combine(modsRoot, "WTT-ServerCommonLib");
            var contentRoot = Path.Combine(modsRoot, "WTT-ContentBackport");
            var commonDll = Path.Combine(commonRoot, "WTT-ServerCommonLib.dll");
            var contentDll = Path.Combine(contentRoot, "WTT-ContentBackport.dll");
            var bundlesPath = Path.Combine(contentRoot, "bundles.json");

            VerifyInstalledAssembly(commonDll, "WTT-ServerCommonLib");
            VerifyInstalledAssembly(contentDll, "WTT-ContentBackport");
            if (!File.Exists(bundlesPath))
            {
                throw new InvalidDataException("Installed WTT-ContentBackport bundles manifest is missing.");
            }

            var bundleRoot = Path.Combine(contentRoot, "bundles");
            var streamingRoot = ResolveStreamingRoot(installRoot, runtimeRoot);
            var entries = ReadEntries(bundlesPath);
            return new WttBundleCatalog(bundleRoot, streamingRoot, entries);
        }

        private static string ResolveStreamingRoot(string installRoot, string runtimeRoot)
        {
            var installCandidate = Path.Combine(
                installRoot,
                "EscapeFromTarkov_Data",
                "StreamingAssets",
                "Windows");
            if (Directory.Exists(installCandidate))
            {
                return installCandidate;
            }

            // Some development layouts put the game data below the SPT
            // runtime directory.  Resolve that explicitly only when the
            // canonical install-root location is absent.
            var runtimeCandidate = Path.Combine(
                runtimeRoot,
                "EscapeFromTarkov_Data",
                "StreamingAssets",
                "Windows");
            if (Directory.Exists(runtimeCandidate))
            {
                return runtimeCandidate;
            }

            throw new InvalidDataException(
                "Target EscapeFromTarkov StreamingAssets/Windows directory is missing under the installed SPT roots.");
        }

        public bool VerifyPrefab(string prefabPath)
        {
            if (string.IsNullOrWhiteSpace(prefabPath))
            {
                return false;
            }

            if (_prefabVerification.TryGetValue(prefabPath, out var cached))
            {
                return cached;
            }

            if (!_entries.ContainsKey(prefabPath)
                || !TryResolveWtt(prefabPath, out var prefabFile))
            {
                _prefabVerification[prefabPath] = false;
                return false;
            }

            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var complete = new HashSet<string>(StringComparer.Ordinal);
            var result = VerifyKey(prefabPath, prefabFile, visiting, complete);
            _prefabVerification[prefabPath] = result;
            return result;
        }

        private bool VerifyKey(
            string key,
            string? knownPath,
            HashSet<string> visiting,
            HashSet<string> complete)
        {
            if (complete.Contains(key))
            {
                return true;
            }

            if (!visiting.Add(key))
            {
                return false;
            }

            if (!_entries.TryGetValue(key, out var entry))
            {
                visiting.Remove(key);
                return false;
            }

            var path = knownPath;
            if (path is null && !TryResolveWtt(key, out path))
            {
                visiting.Remove(key);
                return false;
            }

            if (!VerifyHash(path, entry.DeclaredSha256))
            {
                visiting.Remove(key);
                return false;
            }

            foreach (var dependency in entry.Dependencies)
            {
                if (_entries.ContainsKey(dependency))
                {
                    if (!VerifyKey(dependency, null, visiting, complete))
                    {
                        visiting.Remove(key);
                        return false;
                    }

                    continue;
                }

                // WTT's catalog names Unity-provided roots such as shaders,
                // cubemaps, and defaultmaterial without declaring them in its
                // own manifest.  Require the actual installed streaming file
                // rather than treating the name as proof of availability.
                if (!TryResolveStreaming(dependency, out var installedPath)
                    || !VerifyHash(installedPath, null))
                {
                    visiting.Remove(key);
                    return false;
                }
            }

            visiting.Remove(key);
            complete.Add(key);
            return true;
        }

        private bool TryResolveWtt(string key, out string path)
        {
            path = "";
            if (!IsSafeKey(key))
            {
                return false;
            }

            var candidate = Path.GetFullPath(Path.Combine(_bundleRoot, key.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = Path.GetFullPath(_bundleRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(candidate))
            {
                return false;
            }

            path = candidate;
            return true;
        }

        private bool TryResolveStreaming(string key, out string path)
        {
            path = "";
            if (!IsSafeKey(key))
            {
                return false;
            }

            var candidate = Path.GetFullPath(Path.Combine(_streamingRoot, key.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = Path.GetFullPath(_streamingRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(candidate))
            {
                return false;
            }

            path = candidate;
            return true;
        }

        private bool VerifyHash(string path, string? declaredSha256)
        {
            var cacheKey = path + "\u001f" + (declaredSha256 ?? "");
            if (_hashVerification.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            bool result;
            if (!File.Exists(path))
            {
                _hashVerification[cacheKey] = false;
                return false;
            }

            using (var input = File.OpenRead(path))
            {
                Span<byte> signature = stackalloc byte[8];
                if (input.Length < 32 || input.Read(signature) != signature.Length || !signature.SequenceEqual("UnityFS\0"u8))
                {
                    _hashVerification[cacheKey] = false;
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(declaredSha256))
            {
                // WTT 2.0.2's bundles.json records dependency keys but does
                // not carry SHA-256 declarations.  Existence and the exact
                // catalog key are therefore the available installed identity.
                _ = ManifestRules.Hash(path);
                _hashVerification[cacheKey] = true;
                return true;
            }

            result = string.Equals(
                ManifestRules.Hash(path),
                declaredSha256,
                StringComparison.OrdinalIgnoreCase);
            _hashVerification[cacheKey] = result;
            return result;
        }

        private static Dictionary<string, BundleEntry> ReadEntries(string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("manifest", out var manifest)
                || manifest.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Installed WTT bundles.json has no manifest array.");
            }

            var entries = new Dictionary<string, BundleEntry>(StringComparer.Ordinal);
            foreach (var element in manifest.EnumerateArray())
            {
                if (!element.TryGetProperty("key", out var keyElement)
                    || keyElement.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("Installed WTT bundles.json contains an entry without a key.");
                }

                var key = keyElement.GetString() ?? "";
                if (!IsSafeKey(key) || !entries.TryAdd(key, new BundleEntry(key, ReadDependencies(element), ReadDeclaredHash(element))))
                {
                    throw new InvalidDataException("Installed WTT bundles.json has an invalid or duplicate key: " + key);
                }
            }

            return entries;
        }

        private static IReadOnlyList<string> ReadDependencies(JsonElement element)
        {
            if (!element.TryGetProperty("dependencyKeys", out var dependencies)
                || dependencies.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Installed WTT bundle entry has no dependencyKeys array.");
            }

            var result = new List<string>();
            foreach (var dependency in dependencies.EnumerateArray())
            {
                if (dependency.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("Installed WTT bundle dependency is not a string.");
                }

                var key = dependency.GetString() ?? "";
                if (!IsSafeKey(key))
                {
                    throw new InvalidDataException("Installed WTT bundle dependency is unsafe: " + key);
                }

                result.Add(key);
            }

            return result;
        }

        private static string? ReadDeclaredHash(JsonElement element)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!property.Name.Equals("sha256", StringComparison.OrdinalIgnoreCase)
                    && !property.Name.Equals("hash", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("Installed WTT bundle hash declaration is not a string.");
                }

                var value = property.Value.GetString() ?? "";
                if (value.Length != 64)
                {
                    throw new InvalidDataException("Installed WTT bundle hash declaration is not SHA-256: " + value);
                }

                foreach (var character in value)
                {
                    var hexadecimal = character is >= '0' and <= '9'
                        or >= 'a' and <= 'f'
                        or >= 'A' and <= 'F';
                    if (!hexadecimal)
                    {
                        throw new InvalidDataException("Installed WTT bundle hash declaration is not hexadecimal SHA-256: " + value);
                    }
                }

                return value;
            }

            return null;
        }

        private static bool IsSafeKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)
                || Path.IsPathRooted(key)
                || key.IndexOfAny(new[] { '\\', '\0', ':' }) >= 0)
            {
                return false;
            }

            var parts = key.Split('/');
            foreach (var part in parts)
            {
                if (part is "" or "." or ".."
                    || part.EndsWith(".", StringComparison.Ordinal)
                    || part.EndsWith(" ", StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static void VerifyInstalledAssembly(string path, string name)
        {
            if (!File.Exists(path))
            {
                throw new InvalidDataException("Installed " + name + " assembly is missing: " + path);
            }

            var assembly = AssemblyName.GetAssemblyName(path);
            if (!string.Equals(assembly.Name, name, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Installed assembly identity differs from " + name + ": " + path);
            }

            var version = assembly.Version;
            if (version is null)
            {
                throw new InvalidDataException("Installed " + name + " assembly has no version: " + path);
            }
        }

        private sealed record BundleEntry(
            string Key,
            IReadOnlyList<string> Dependencies,
            string? DeclaredSha256);
    }

}
