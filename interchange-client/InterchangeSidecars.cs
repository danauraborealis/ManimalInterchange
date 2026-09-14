using System;
using System.Collections.Generic;
using System.IO;
using Koenigz.PerfectCulling.EFT;
using Manimal.Interchange.Shared;
using UnityEngine;

namespace Manimal.Interchange.Client;

internal static class InterchangeSidecars
{
    private static readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase);

    internal static void Activate(ContentManifest manifest)
    {
        Files.Clear();
        foreach (var entry in manifest.Sidecars)
        {
            const string prefix = "StreamingAssets/";
            if (!entry.Path.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException("Sidecar must preserve its StreamingAssets relative path");
            Files.Add(entry.Path.Substring(prefix.Length), ManifestRules.Resolve(Plugin.Root, entry.Path));
        }
    }

    internal static void Clear() => Files.Clear();

    private static bool Owns(Component owner) => owner && SceneLoader.Owns(owner.gameObject.scene.path);

    private static string Resolve(Component owner, string relative)
    {
        if (!Owns(owner)) return "";
        relative = relative.Replace('\\', '/');
        ManifestRules.ValidateRelativePath(relative);
        if (!Files.TryGetValue(relative, out var path))
            throw new InvalidDataException("Replacement Interchange requested an undeclared sidecar: " + relative);
        return path;
    }

    internal static void PackedPath(PerfectCullingAdaptiveGrid grid, ref string result)
    {
        if (!Owns(grid)) return;
        result = Resolve(grid, "Culling_Data/" + grid.GridHash + "_packed_cull.bytes");
    }

    internal static void AudioPath(ref string dataPath, MonoBehaviour runner)
    {
        var path = Resolve(runner, dataPath);
        if (path.Length != 0) dataPath = path;
    }

    internal static void XrAbsolutePath(Component owner, ref string result)
    {
        if (!Owns(owner)) return;
        var streaming = Path.GetFullPath(Application.streamingAssetsPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var original = Path.GetFullPath(result);
        if (!original.StartsWith(streaming, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Replacement Interchange acoustic path is outside its StreamingAssets source");
        result = Resolve(owner, original.Substring(streaming.Length));
    }

    internal static void XrAsyncPath(Component owner, ref string relative)
    {
        var path = Resolve(owner, relative);
        if (path.Length != 0)
            relative = Path.GetRelativePath(Application.streamingAssetsPath, path).Replace('\\', '/');
    }
}
