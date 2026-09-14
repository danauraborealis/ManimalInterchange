using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Interchange.Client;

internal static class AreaLightBindings
{
    private static readonly Dictionary<int, Scope> Scopes = new();
    private static readonly HashSet<string> ProbePaths = new(StringComparer.OrdinalIgnoreCase);
    private sealed class PathScope : IDisposable
    {
        private readonly string _path;
        internal PathScope(string path) => _path = path;
        public void Dispose() => ProbePaths.Remove(_path);
    }
    internal static IDisposable ClaimProbePath(string path)
    {
        if (string.IsNullOrEmpty(path) || !ProbePaths.Add(path)) throw new InvalidOperationException("Duplicate or empty area-light probe path");
        return new PathScope(path);
    }

    internal sealed class Scope : IDisposable
    {
        private readonly int _sceneHandle;
        internal readonly Shader Proxy;
        internal readonly Shader Shadow;
        internal readonly Shader Blur;
        internal Scope(Scene scene)
        {
            _sceneHandle = scene.handle;
            Proxy = Resolve("Hidden/AreaLight");
            Shadow = Resolve("Hidden/Shadowmap");
            Blur = Resolve("Hidden/BlurShadowmap");
        }
        public void Dispose() => Scopes.Remove(_sceneHandle);
    }

    internal static Scope ClaimScene(Scene scene)
    {
        // Awake runs during scene activation, before isLoaded must be true.
        if (!scene.IsValid() || Scopes.ContainsKey(scene.handle))
            throw new InvalidOperationException("Invalid or already owned area-light scene");
        var scope = new Scope(scene);
        Scopes.Add(scene.handle, scope);
        return scope;
    }

    private static Shader Resolve(string name)
    {
        if (ShadersFinder.SHADERS.TryGetValue(name, out var registered) && registered && registered.isSupported)
            return registered;
        var found = Shader.Find(name);
        if (found && found.isSupported) return found;
        throw new InvalidOperationException("Required target area-light shader unavailable: " + name);
    }

    internal static void BeforeInit(AreaLight light)
    {
        var scene = light.gameObject.scene;
        if (!Scopes.TryGetValue(scene.handle, out var scope))
        {
            if (!SceneLoader.Owns(scene.path) && !ProbePaths.Contains(scene.path)) return;
            scope = ClaimScene(scene);
        }
        light.m_ProxyShader = scope.Proxy;
        light.m_ShadowmapShader = scope.Shadow;
        light.m_BlurShadowmapShader = scope.Blur;
        if (!light.TryGetComponent<MeshFilter>(out _)) light.gameObject.AddComponent<MeshFilter>();
        if (!light.TryGetComponent<MeshRenderer>(out _)) light.gameObject.AddComponent<MeshRenderer>();
    }

    internal static void SceneUnloaded(Scene scene)
    {
        if (Scopes.TryGetValue(scene.handle, out var scope)) scope.Dispose();
    }
}

internal sealed class AreaLightInitPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(AreaLight), "Init", Type.EmptyTypes);
    [PatchPrefix]
    private static void Prefix(AreaLight __instance) => AreaLightBindings.BeforeInit(__instance);
}
