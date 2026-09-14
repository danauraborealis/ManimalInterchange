using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Manimal.Interchange.Shared;

public static class ManifestRules
{
    public const string FileName = "interchange-content.json";
    public const string CapabilityRoute = "/manimal/interchange/capability";
    public static readonly string[] SceneSuffixes = { "Scripts", "Terrain", "2", "indoor", "outdoor", "IDEA", "Shops", "parking_work", "OLI", "GOSHAN", "Shops_Floor2", "indoor_buildup", "light", "DesignStuff", "DesignMain", "AI", "Sound", "Culling" };
    public static readonly string[] RequiredFeatures = { "references", "visuals", "collision", "interactions", "extracts", "hazards", "navigation", "audio", "culling", "serverData", "items", "raidValidation" };

    public static void Validate(ContentManifest manifest)
    {
        if (manifest == null || manifest.Schema != 1 || string.IsNullOrWhiteSpace(manifest.ContentId))
            throw new InvalidDataException("Invalid Interchange content identity/schema");
        if (manifest.ModVersion != ModIdentity.Version || manifest.TargetClientBuild != ModIdentity.TargetClientBuild)
            throw new InvalidDataException("Manifest does not match this mod/target build");
        if (manifest.Mode != "development" && manifest.Mode != "probe" && manifest.Mode != "test" && manifest.Mode != "rework")
            throw new InvalidDataException("Unknown content mode");
        if (manifest.Ready && manifest.Mode != "rework") throw new InvalidDataException("Development content cannot claim release readiness");
        if (manifest.Scenes == null || manifest.Scenes.Count != SceneSuffixes.Length)
            throw new InvalidDataException("All 18 ordered Interchange scenes are required");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < SceneSuffixes.Length; i++)
        {
            var scene = manifest.Scenes[i];
            var expected = "Assets/Content/Locations/Shopping_Mall/Shopping_Mall_" + SceneSuffixes[i] + ".unity";
            if (scene == null || scene.OriginalPath != expected || scene.OnlyOffline != (SceneSuffixes[i] == "AI"))
                throw new InvalidDataException("Scene order/path/offline flag differs from the audited preset");
            ValidateRelativePath(scene.ReplacementPath);
            if (!scene.ReplacementPath.StartsWith("Assets/", StringComparison.Ordinal) || !scene.ReplacementPath.EndsWith("_MI.unity", StringComparison.Ordinal)
                || !names.Add(System.IO.Path.GetFileNameWithoutExtension(scene.ReplacementPath)))
                throw new InvalidDataException("Replacement scenes must have unique _MI names");
        }
        if (manifest.Bundles == null || manifest.Bundles.Count == 0) throw new InvalidDataException("No bundles supplied");
        var payloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ValidateFiles(manifest.Bundles, payloads);
        ValidateFiles(manifest.Sidecars, payloads);
        ValidateFiles(manifest.ServerFiles, payloads);
        ValidateFiles(manifest.ItemFiles, payloads);
        ValidateFiles(manifest.NativeFiles, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        foreach (var file in manifest.NativeFiles)
            if (!Regex.IsMatch(file.Path, @"^(?:(?:sharedassets[0-9]+|resources|globalgamemanagers)\.assets(?:\.resS)?|(?:sharedassets[0-9]+|resources)\.resource)$"))
                throw new InvalidDataException("Unsupported native donor: " + file.Path);
        if (manifest.Features == null) throw new InvalidDataException("Missing feature ledger");
        if (manifest.Ready)
            foreach (var feature in RequiredFeatures)
                if (!manifest.Features.TryGetValue(feature, out var passed) || !passed)
                    throw new InvalidDataException("Unverified release feature: " + feature);
        if ((manifest.Mode == "test" || manifest.Mode == "rework") && manifest.ServerFiles.Count != 7)
            throw new InvalidDataException("Full-map content needs all seven coordinated server files");
        if (manifest.Mode == "probe" && (manifest.ServerFiles.Count != 0 || manifest.ItemFiles.Count != 0))
            throw new InvalidDataException("Loader probes must not replace gameplay data");
    }

    private static void ValidateFiles(List<PayloadFile> files, HashSet<string> paths)
    {
        if (files == null) throw new InvalidDataException("Missing file list");
        foreach (var file in files)
        {
            if (file == null || !paths.Add(file.Path) || file.Bytes <= 0) throw new InvalidDataException("Invalid or duplicate payload");
            ValidateRelativePath(file.Path);
            if (!Regex.IsMatch(file.Sha256 ?? "", "^[a-f0-9]{64}$")) throw new InvalidDataException("Invalid SHA-256");
        }
    }

    public static void ValidateRelativePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || System.IO.Path.IsPathRooted(relative) || relative.IndexOfAny(new[] { ':', '\\', '\0' }) >= 0)
            throw new InvalidDataException("Expected relative forward-slash path");
        foreach (var part in relative.Split('/'))
            if (part == "" || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" "))
                throw new InvalidDataException("Invalid relative path segment");
    }

    public static string Resolve(string root, string relative)
    {
        ValidateRelativePath(relative);
        var prefix = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(prefix, relative));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Payload escapes root");
        return path;
    }

    public static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        using var hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
    }

    public static void VerifyFile(string root, PayloadFile file)
    {
        var path = Resolve(root, file.Path);
        if (!File.Exists(path) || new FileInfo(path).Length != file.Bytes || Hash(path) != file.Sha256)
            throw new InvalidDataException("Missing or mismatched payload: " + file.Path);
    }

    public static void CheckCapability(ContentManifest manifest, string hash, ServerCapability server, bool probe)
    {
        if (server == null || server.Schema != 1 || !server.Ready || server.ModVersion != ModIdentity.Version
            || server.ContentId != manifest.ContentId || server.ManifestSha256 != hash || server.Selection != (probe ? "probe" : "rework"))
            throw new InvalidDataException("Client/server content contract mismatch");
        if (probe != (manifest.Mode == "probe")) throw new InvalidDataException("Content mode mismatch");
    }
}
