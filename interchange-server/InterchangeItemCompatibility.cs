using System.Collections.ObjectModel;
using Manimal.Interchange.Shared;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Utils;

namespace Manimal.Interchange.Server;

/// <summary>
/// Validates and applies the 16 Interchange item templates which are absent
/// from the effective SPT 4.1.5 database.
///
/// This class deliberately owns only an in-memory template dictionary.  A
/// caller must verify the staged file through an ItemFiles manifest entry (or
/// an explicit path/byte count/SHA-256) before this class deserializes it.  It
/// does not edit a database file, register a server hook, or start a server.
/// </summary>
public static class InterchangeItemCompatibility
{
    public const string InfoParentId = "5448ecbe4bdc2d60728b4568";
    public const string StackableParentId = "5661632d4bdc2d903d8b456b";
    public const string GenericItemParentId = "54009119af1c881c07000029";

    public const string PreparedTemplateFileName = "backend-items-templates.json";
    public const string PreparedLocaleFileName = "backend-items-locales-en.json";

    private static readonly string[] RequiredItemIdValues =
    [
        "6877c84d020406d3ea060551",
        "688895322f6b5b76380e6af5",
        "6888958794cca0a80b070ae9",
        "6888965ac122cae7a20765fa",
        "68d2f69e91c2fa84e2044a08",
        "68d2f7334aae290cf704e36d",
        "68d2f77e5f98276b7503c330",
        "68d2f85af63f06b7590ce310",
        "68d302814aae290cf704e375",
        "68d3054f0c834c20c00a81da",
        "68d3059318d70f97e704ad7f",
        "68d305c35f98276b7503c336",
        "68d305fc8c12620073059936",
        "68e63d4794fb983cb7098d2d",
        "6a31807f17005505b70d5827",
        "6a31824878450ec91c0ea1ae"
    ];

    private static readonly HashSet<string> RequiredItemIds =
        new(RequiredItemIdValues, StringComparer.Ordinal);

    /// <summary>
    /// Reads the prepared item dictionary after checking the declared byte
    /// count and SHA-256.  Verification happens before JsonUtil sees any
    /// content.
    /// </summary>
    public static Dictionary<MongoId, TemplateItem> ReadPrepared(
        JsonUtil json,
        string root,
        PayloadFile declaration)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(declaration);

        ManifestRules.VerifyFile(root, declaration);
        var path = ManifestRules.Resolve(root, declaration.Path);
        var text = File.ReadAllText(path);
        var result = json.Deserialize<Dictionary<MongoId, TemplateItem>>(text);
        if (result is null || result.Count == 0)
        {
            throw new InvalidDataException("Prepared backend item dictionary is empty: " + declaration.Path);
        }

        return result;
    }

    /// <summary>
    /// Reads an item file selected from the explicit ContentManifest.ItemFiles
    /// list.  The path must occur exactly once; ServerFiles are not consulted.
    /// </summary>
    public static Dictionary<MongoId, TemplateItem> ReadPrepared(
        JsonUtil json,
        string root,
        ContentManifest manifest,
        string relativePath)
    {
        return ReadPrepared(json, root, FindItemFile(manifest, relativePath));
    }

    /// <summary>
    /// Reads the flat English locale fragment after the same manifest
    /// identity check used for templates.  The three required item labels are
    /// checked for every bounded ID; referenced note/tape content keys remain
    /// optional when the upstream locale snapshot does not contain them.
    /// </summary>
    public static Dictionary<string, string> ReadPreparedLocales(
        JsonUtil json,
        string root,
        PayloadFile declaration)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(declaration);

        ManifestRules.VerifyFile(root, declaration);
        var path = ManifestRules.Resolve(root, declaration.Path);
        var text = File.ReadAllText(path);
        var result = json.Deserialize<Dictionary<string, string>>(text);
        if (result is null || result.Count == 0)
        {
            throw new InvalidDataException("Prepared backend item locale fragment is empty: " + declaration.Path);
        }

        foreach (var itemIdValue in RequiredItemIdValues)
        {
            if (!result.ContainsKey(itemIdValue + " Name")
                || !result.ContainsKey(itemIdValue + " ShortName")
                || !result.ContainsKey(itemIdValue + " Description"))
            {
                throw new InvalidDataException("Prepared backend item locale labels are incomplete: " + itemIdValue);
            }
        }

        return result;
    }

    /// <summary>Reads the locale fragment selected from ContentManifest.ItemFiles.</summary>
    public static Dictionary<string, string> ReadPreparedLocales(
        JsonUtil json,
        string root,
        ContentManifest manifest,
        string relativePath)
    {
        return ReadPreparedLocales(json, root, FindItemFile(manifest, relativePath));
    }

    /// <summary>
    /// Reads a prepared item file when the caller has an explicit path and
    /// content identity.  This overload is useful before a full manifest is
    /// available and still verifies bytes and SHA-256 before deserialization.
    /// </summary>
    public static Dictionary<MongoId, TemplateItem> ReadPrepared(
        JsonUtil json,
        string root,
        string relativePath,
        long bytes,
        string sha256)
    {
        return ReadPrepared(json, root, new PayloadFile
        {
            Path = relativePath,
            Bytes = bytes,
            Sha256 = sha256
        });
    }

    /// <summary>Reads a locale fragment with an explicit path, byte count, and SHA-256.</summary>
    public static Dictionary<string, string> ReadPreparedLocales(
        JsonUtil json,
        string root,
        string relativePath,
        long bytes,
        string sha256)
    {
        return ReadPreparedLocales(json, root, new PayloadFile
        {
            Path = relativePath,
            Bytes = bytes,
            Sha256 = sha256
        });
    }

    /// <summary>
    /// Validates the live target templates and prepared records.  The caller
    /// supplies the resource check so the WTT bundle/dependency policy remains
    /// owned by the integration layer.  A null verifier is rejected instead
    /// of silently accepting an unresolved prefab.
    /// </summary>
    public static ValidationReport Validate(
        IReadOnlyDictionary<MongoId, TemplateItem> liveTemplates,
        IReadOnlyDictionary<MongoId, TemplateItem> preparedTemplates,
        Func<string, bool>? prefabBundleAvailable,
        bool allowExistingRecords = false)
    {
        return ValidateCore(
            liveTemplates,
            preparedTemplates,
            prefabBundleAvailable,
            allowExistingRecords,
            requirePrefabVerifier: true);
    }

    /// <summary>
    /// Deserializes and validates a staged dictionary without mutating the
    /// live target collection.  The returned set is immutable from the
    /// caller's perspective and can be committed later.
    /// </summary>
    public static PreparedItemSet Prepare(
        IReadOnlyDictionary<MongoId, TemplateItem> liveTemplates,
        IReadOnlyDictionary<MongoId, TemplateItem> preparedTemplates,
        Func<string, bool> prefabBundleAvailable,
        bool allowExistingRecords = false)
    {
        ArgumentNullException.ThrowIfNull(prefabBundleAvailable);
        var report = Validate(liveTemplates, preparedTemplates, prefabBundleAvailable, allowExistingRecords);
        report.ThrowIfInvalid();

        var copy = new Dictionary<MongoId, TemplateItem>();
        foreach (var itemIdValue in RequiredItemIdValues)
        {
            var itemId = new MongoId(itemIdValue);
            copy.Add(itemId, preparedTemplates[itemId]);
        }

        return new PreparedItemSet(copy, prefabBundleAvailable);
    }

    /// <summary>
    /// Reads, verifies, deserializes, and prepares an item file declared in
    /// ContentManifest.ItemFiles.  It still performs no live mutation.
    /// </summary>
    public static PreparedItemSet Prepare(
        JsonUtil json,
        string root,
        ContentManifest manifest,
        string relativePath,
        IReadOnlyDictionary<MongoId, TemplateItem> liveTemplates,
        Func<string, bool> prefabBundleAvailable,
        bool allowExistingRecords = false)
    {
        var prepared = ReadPrepared(json, root, manifest, relativePath);
        return Prepare(liveTemplates, prepared, prefabBundleAvailable, allowExistingRecords);
    }

    /// <summary>
    /// Reads and prepares a staged item file when its explicit content
    /// identity is supplied by the caller rather than a full manifest.
    /// </summary>
    public static PreparedItemSet Prepare(
        JsonUtil json,
        string root,
        string relativePath,
        long bytes,
        string sha256,
        IReadOnlyDictionary<MongoId, TemplateItem> liveTemplates,
        Func<string, bool> prefabBundleAvailable,
        bool allowExistingRecords = false)
    {
        var prepared = ReadPrepared(json, root, relativePath, bytes, sha256);
        return Prepare(liveTemplates, prepared, prefabBundleAvailable, allowExistingRecords);
    }

    /// <summary>
    /// Commits all 16 records to the in-memory template dictionary.  The
    /// operation snapshots every affected key and restores that snapshot if
    /// any assignment fails.  Set <paramref name="replaceExisting"/> only
    /// when the caller explicitly owns replacement of an existing record.
    /// </summary>
    public static CommitReceipt Commit(
        IDictionary<MongoId, TemplateItem> liveTemplates,
        PreparedItemSet prepared,
        bool replaceExisting = false)
    {
        ArgumentNullException.ThrowIfNull(liveTemplates);
        ArgumentNullException.ThrowIfNull(prepared);

        // Prepare() has already run the required asset check.  Commit repeats
        // structural checks against the current live dictionary so a change
        // between preparation and commit cannot partially install the set.
        var liveView = liveTemplates as IReadOnlyDictionary<MongoId, TemplateItem>
            ?? new Dictionary<MongoId, TemplateItem>(liveTemplates);
        var report = ValidateCore(
            liveView,
            prepared.Templates,
            prefabBundleAvailable: null,
            allowExistingRecords: replaceExisting,
            requirePrefabVerifier: false);
        report.ThrowIfInvalid();

        var snapshot = new List<TemplateSnapshot>();
        foreach (var itemIdValue in RequiredItemIdValues)
        {
            var itemId = new MongoId(itemIdValue);
            if (liveTemplates.TryGetValue(itemId, out var existing))
            {
                snapshot.Add(new TemplateSnapshot(itemId, true, existing));
            }
            else
            {
                snapshot.Add(new TemplateSnapshot(itemId, false, null));
            }
        }

        try
        {
            foreach (var itemIdValue in RequiredItemIdValues)
            {
                var itemId = new MongoId(itemIdValue);
                liveTemplates[itemId] = prepared.Templates[itemId];
            }
        }
        catch (Exception error)
        {
            Restore(liveTemplates, snapshot);
            throw new InvalidOperationException("Backend item template commit rolled back after an assignment failure.", error);
        }

        return new CommitReceipt(liveTemplates, snapshot);
    }

    private static PayloadFile FindItemFile(ContentManifest manifest, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.ItemFiles is null)
        {
            throw new InvalidDataException("Manifest has no ItemFiles list");
        }

        PayloadFile? declaration = null;
        foreach (var candidate in manifest.ItemFiles)
        {
            if (candidate is null)
            {
                throw new InvalidDataException("Manifest contains a null ItemFiles declaration");
            }

            if (!string.Equals(candidate.Path, relativePath, StringComparison.Ordinal))
            {
                continue;
            }

            if (declaration is not null)
            {
                throw new InvalidDataException("Duplicate ItemFiles declaration: " + relativePath);
            }

            declaration = candidate;
        }

        return declaration ?? throw new InvalidDataException("Missing ItemFiles declaration: " + relativePath);
    }

    private static ValidationReport ValidateCore(
        IReadOnlyDictionary<MongoId, TemplateItem> liveTemplates,
        IReadOnlyDictionary<MongoId, TemplateItem> preparedTemplates,
        Func<string, bool>? prefabBundleAvailable,
        bool allowExistingRecords,
        bool requirePrefabVerifier)
    {
        ArgumentNullException.ThrowIfNull(liveTemplates);
        ArgumentNullException.ThrowIfNull(preparedTemplates);

        var errors = new List<string>();
        if (preparedTemplates.Count != RequiredItemIdValues.Length)
        {
            errors.Add($"Prepared item count is {preparedTemplates.Count}; expected {RequiredItemIdValues.Length}.");
        }

        foreach (var pair in preparedTemplates)
        {
            var key = pair.Key.ToString();
            if (!RequiredItemIds.Contains(key))
            {
                errors.Add("Prepared dictionary contains an out-of-scope item: " + key);
            }
        }

        if (requirePrefabVerifier && prefabBundleAvailable is null)
        {
            errors.Add("A prefab bundle verifier is required before backend item preparation.");
        }

        var targetParents = new HashSet<string>(StringComparer.Ordinal)
        {
            InfoParentId,
            StackableParentId,
            GenericItemParentId
        };
        foreach (var parentIdValue in targetParents)
        {
            var parentId = new MongoId(parentIdValue);
            if (!liveTemplates.TryGetValue(parentId, out var parent))
            {
                errors.Add("Missing target constructor parent: " + parentIdValue);
                continue;
            }

            if (parent is null || !string.Equals(parent.Type, "Node", StringComparison.Ordinal))
            {
                errors.Add("Target constructor parent is not a Node: " + parentIdValue);
            }
        }

        foreach (var itemIdValue in RequiredItemIdValues)
        {
            var itemId = new MongoId(itemIdValue);
            if (!preparedTemplates.TryGetValue(itemId, out var template))
            {
                errors.Add("Prepared dictionary is missing item: " + itemIdValue);
                continue;
            }

            if (template is null)
            {
                errors.Add("Prepared dictionary contains a null item: " + itemIdValue);
                continue;
            }

            if (!allowExistingRecords && liveTemplates.ContainsKey(itemId))
            {
                errors.Add("Target already contains prepared item: " + itemIdValue);
            }

            if (template.Id != itemId)
            {
                errors.Add("Template _id does not match dictionary key: " + itemIdValue);
            }

            if (!string.Equals(template.Type, "Item", StringComparison.Ordinal))
            {
                errors.Add("Template _type is not Item: " + itemIdValue);
            }

            var expectedParent = new MongoId(TargetParentId(itemIdValue));
            if (template.Parent != expectedParent)
            {
                errors.Add("Template _parent does not select the reviewed target constructor: " + itemIdValue);
            }

            if (string.IsNullOrWhiteSpace(template.Name))
            {
                errors.Add("Template _name is empty: " + itemIdValue);
            }

            if (template.Properties is null)
            {
                errors.Add("Template _props is null: " + itemIdValue);
                continue;
            }

            if (template.Properties.Prefab is null || string.IsNullOrWhiteSpace(template.Properties.Prefab.Path))
            {
                errors.Add("Template Prefab.path is empty: " + itemIdValue);
            }
            else if (prefabBundleAvailable is not null)
            {
                CheckPrefab(prefabBundleAvailable, template.Properties.Prefab.Path, itemIdValue, errors);
            }

            var usePrefab = template.Properties.UsePrefab;
            if (usePrefab is not null && !string.IsNullOrWhiteSpace(usePrefab.Path) && prefabBundleAvailable is not null)
            {
                CheckPrefab(prefabBundleAvailable, usePrefab.Path, itemIdValue, errors);
            }
        }

        return new ValidationReport(errors);
    }

    private static void CheckPrefab(
        Func<string, bool> prefabBundleAvailable,
        string prefabPath,
        string itemId,
        List<string> errors)
    {
        bool available;
        try
        {
            available = prefabBundleAvailable(prefabPath);
        }
        catch (Exception error)
        {
            errors.Add($"Prefab verifier threw for {itemId} ({prefabPath}): {error.Message}");
            return;
        }

        if (!available)
        {
            errors.Add($"Prefab bundle is unavailable for {itemId}: {prefabPath}");
        }
    }

    private static string TargetParentId(string itemId)
    {
        if (itemId == RequiredItemIdValues[^1] || itemId == RequiredItemIdValues[^2])
        {
            return StackableParentId;
        }

        return InfoParentId;
    }

    private static void Restore(
        IDictionary<MongoId, TemplateItem> target,
        IReadOnlyList<TemplateSnapshot> snapshot)
    {
        foreach (var entry in snapshot)
        {
            if (entry.WasPresent)
            {
                target[entry.Id] = entry.Previous!;
            }
            else
            {
                target.Remove(entry.Id);
            }
        }
    }

    internal sealed class TemplateSnapshot
    {
        public TemplateSnapshot(MongoId id, bool wasPresent, TemplateItem? previous)
        {
            Id = id;
            WasPresent = wasPresent;
            Previous = previous;
        }

        public MongoId Id { get; }
        public bool WasPresent { get; }
        public TemplateItem? Previous { get; }
    }

    public sealed class ValidationReport
    {
        internal ValidationReport(IReadOnlyList<string> errors)
        {
            Errors = new ReadOnlyCollection<string>(new List<string>(errors));
        }

        public IReadOnlyList<string> Errors { get; }
        public bool IsValid => Errors.Count == 0;

        public void ThrowIfInvalid()
        {
            if (!IsValid)
            {
                throw new InvalidDataException(
                    "Backend item compatibility validation failed:" + Environment.NewLine + string.Join(Environment.NewLine, Errors));
            }
        }
    }

    public sealed class PreparedItemSet
    {
        internal PreparedItemSet(
            IDictionary<MongoId, TemplateItem> templates,
            Func<string, bool> prefabBundleAvailable)
        {
            Templates = new ReadOnlyDictionary<MongoId, TemplateItem>(
                new Dictionary<MongoId, TemplateItem>(templates));
            PrefabBundleAvailable = prefabBundleAvailable;
        }

        public IReadOnlyDictionary<MongoId, TemplateItem> Templates { get; }

        // Retained so a later integration layer can re-check the same policy
        // before loading a replacement scene or loot table.
        internal Func<string, bool> PrefabBundleAvailable { get; }
    }

    public sealed class CommitReceipt
    {
        private readonly IDictionary<MongoId, TemplateItem> _target;
        private readonly IReadOnlyList<TemplateSnapshot> _snapshot;
        private bool _rolledBack;

        internal CommitReceipt(
            IDictionary<MongoId, TemplateItem> target,
            IReadOnlyList<TemplateSnapshot> snapshot)
        {
            _target = target;
            _snapshot = snapshot;
        }

        public bool IsRolledBack => _rolledBack;

        /// <summary>
        /// Restores every affected key to the state captured immediately
        /// before Commit.  The caller must invoke this during startup while
        /// it owns the same template dictionary.
        /// </summary>
        public void Rollback()
        {
            if (_rolledBack)
            {
                return;
            }

            Restore(_target, _snapshot);
            _rolledBack = true;
        }
    }
}
