using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityStandardAssets.ImageEffects;

namespace Manimal.Interchange.Client;

// The authored bundles carry retail-compiled copies of shaders the target game
// also ships (Hidden/WriteDepth, Hidden/Tonemapper, Standard, p0/*...). Scene
// components and materials reference the bundle copies, so our map runs retail
// programs inside the 0.16.9 render pipeline while every other map runs the
// game's own. Rebind by name to the game's copies wherever one exists; shaders
// with no native counterpart keep their retail program.
internal static class NativeShaderBindings
{
    private static readonly Dictionary<string, Shader> Native = new(StringComparer.Ordinal);
    private static readonly HashSet<string> ReportedMissing = new(StringComparer.Ordinal);
    private static readonly Dictionary<Material, string> AuthoredNames = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        SceneManager.sceneLoaded += SceneLoaded;
        SceneManager.sceneUnloaded += SceneUnloaded;
    }

    private static void SceneUnloaded(Scene scene)
    {
        if (!SceneLoader.Owns(scene.path)) return;
        for (var i = 0; i < SceneManager.sceneCount; i++)
        {
            var remaining = SceneManager.GetSceneAt(i);
            if (remaining.isLoaded && remaining.handle != scene.handle && SceneLoader.Owns(remaining.path)) return;
        }
        Native.Clear();
        ReportedMissing.Clear();
        AuthoredNames.Clear();
    }

    private static void SceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!SceneLoader.Owns(scene.path)) return;
        if (!Plugin.RebindNativeShaders.Value && !Plugin.RebindMaterialShaders.Value) return;
        try
        {
            if (Native.Count == 0) CaptureNative();
            var fields = 0;
            var materials = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (Plugin.RebindNativeShaders.Value) fields += RebindComponentFields(root);
                if (Plugin.RebindMaterialShaders.Value) materials += RebindMaterials(root);
            }
            Plugin.Log.LogInfo("Interchange native shaders: " + scene.name + " rebound " + fields + " component shader field(s) and " + materials + " material(s) to the game's copies.");
        }
        catch (Exception error)
        {
            Plugin.Log.LogError("Interchange native shader rebind failed for " + scene.name + ": " + error);
        }
    }

    // identity source is the game's own shaders bundle â€” Shader.Find can hand
    // back one of our bundle copies once they are loaded
    private static void CaptureNative()
    {
        foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
        {
            if (!bundle || !string.Equals(bundle.name, "shaders", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var shader in bundle.LoadAllAssets<Shader>())
            {
                if (shader && shader.isSupported && !Native.ContainsKey(shader.name)) Native.Add(shader.name, shader);
            }
        }
        foreach (var entry in ShadersFinder.SHADERS)
        {
            if (entry.Value && entry.Value.isSupported && !Native.ContainsKey(entry.Key)) Native.Add(entry.Key, entry.Value);
        }
        Plugin.Log.LogInfo("Interchange native shaders: captured " + Native.Count + " game shader(s) for rebinding.");
    }

    private static Shader? Resolve(string name)
    {
        if (Native.TryGetValue(name, out var shader) && shader) return shader;
        var found = Shader.Find(name);
        if (found && found.isSupported && !IsAuthored(found))
        {
            Native[name] = found;
            return found;
        }
        return null;
    }

    // our bundle copies live in loaded bundles whose names we own; anything the
    // game loaded itself is not
    private static bool IsAuthored(Shader shader)
    {
        foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
        {
            if (!bundle || !bundle.name.StartsWith("manimal_interchange_", StringComparison.Ordinal)) continue;
            if (bundle.Contains(shader.name)) return true;
        }
        return false;
    }

    private static bool Swap(ref Shader field, string owner)
    {
        if (!field) return false;
        var native = Resolve(field.name);
        if (!native) { ReportMissing(field.name, owner); return false; }
        if (field == native) return false;
        field = native!;
        return true;
    }

    private static int RebindComponentFields(GameObject root)
    {
        var count = 0;
        foreach (var ambient in root.GetComponentsInChildren<AmbientLight>(true))
        {
            if (Swap(ref ambient.DepthWriteShader, "AmbientLight.DepthWriteShader")) count++;
            if (Swap(ref ambient.ClearStencilShader, "AmbientLight.ClearStencilShader")) count++;
            if (Swap(ref ambient.WriteStencilShader, "AmbientLight.WriteStencilShader")) count++;
            if (Swap(ref ambient.ScreenAmbientShader, "AmbientLight.ScreenAmbientShader")) count++;
            if (Swap(ref ambient.AnalyticSourceShader, "AmbientLight.AnalyticSourceShader")) count++;
        }
        foreach (var wet in root.GetComponentsInChildren<WetRenderer>(true))
            if (Swap(ref wet.WetShader, "WetRenderer.WetShader")) count++;
        foreach (var snow in root.GetComponentsInChildren<SnowWetRenderer>(true))
            if (Swap(ref snow.WetShader, "SnowWetRenderer.WetShader")) count++;
        foreach (var tone in root.GetComponentsInChildren<Tonemapping>(true))
            if (Swap(ref tone.tonemapper, "Tonemapping.tonemapper")) count++;
        return count;
    }

    private static int RebindMaterials(GameObject root)
    {
        var count = 0;
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            var shared = renderer.sharedMaterials;
            for (var i = 0; i < shared.Length; i++)
                if (Rebind(shared[i])) count++;
        }
        foreach (var snow in root.GetComponentsInChildren<SnowWetRenderer>(true))
        {
            if (Rebind(snow._wetMaterial)) count++;
            if (Rebind(snow._snowCopyMaterial)) count++;
            if (Rebind(snow._snowSparkling)) count++;
        }
        return count;
    }

    private static bool Rebind(Material material)
    {
        if (!material || !material.shader) return false;
        // remember the authored name; after a swap material.shader.name is the
        // same string, but a repeat load must not treat the native as authored
        if (!AuthoredNames.TryGetValue(material, out var name))
        {
            name = material.shader.name;
            AuthoredNames.Add(material, name);
        }
        var native = Resolve(name);
        if (!native) { ReportMissing(name, material.name); return false; }
        if (material.shader == native) return false;
        var queue = material.renderQueue;
        var keywords = material.shaderKeywords;
        material.shader = native;
        material.renderQueue = queue;
        material.shaderKeywords = keywords;
        return true;
    }

    private static void ReportMissing(string shader, string owner)
    {
        if (ReportedMissing.Add(shader))
            Plugin.Log.LogInfo("Interchange native shaders: no game copy of '" + shader + "' (first seen on " + owner + "); keeping the authored program.");
    }
}
