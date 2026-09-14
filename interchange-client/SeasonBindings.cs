using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using JBooth.MicroSplat;
using Manimal.Interchange.Components;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.Interchange.Client;

internal static class SeasonBindings
{
    private static readonly Dictionary<SeasonalMesh, MeshBinding> Meshes = new();
    private static readonly Dictionary<int, SeasonsMaterialsFixer> ProbeFixers = new();
    internal static int RegisteredMeshes => Meshes.Count;
    private sealed class ProbeScope : IDisposable
    {
        private readonly int _handle;
        internal ProbeScope(int handle) => _handle = handle;
        public void Dispose() => ProbeFixers.Remove(_handle);
    }
    internal static IDisposable ClaimProbeFixer(int sceneHandle, SeasonsMaterialsFixer fixer)
    {
        ProbeFixers.Add(sceneHandle, fixer);
        return new ProbeScope(sceneHandle);
    }

    internal static void Enable()
    {
        SeasonalMesh.Started += Register;
        SeasonalMesh.Destroyed += Remove;
    }

    internal static void Disable()
    {
        SeasonalMesh.Started -= Register;
        SeasonalMesh.Destroyed -= Remove;
        foreach (var binding in Meshes.Values) binding.Dispose();
        Meshes.Clear();
    }

    internal static void Register(SeasonalMesh mesh)
    {
        if (Meshes.ContainsKey(mesh)) return;
        var fixer = ProbeFixers.TryGetValue(mesh.gameObject.scene.handle, out var owned) ? owned : SeasonsMaterialsFixer.Instance;
        if (!fixer) throw new InvalidOperationException("Seasonal mesh requires the SPT season controller: " + mesh.SourceKey);
        var binding = new MeshBinding(mesh, fixer);
        Meshes.Add(mesh, binding);
        try { fixer.Add(binding); }
        catch { Meshes.Remove(mesh); binding.Dispose(); throw; }
    }

    internal static void Remove(SeasonalMesh mesh)
    {
        if (!Meshes.TryGetValue(mesh, out var binding)) return;
        Meshes.Remove(mesh);
        binding.Dispose();
    }

    private sealed class MeshBinding : ISeasonsMaterial, IDisposable
    {
        private readonly SeasonalMesh _mesh;
        private readonly SeasonsMaterialsFixer _fixer;
        private readonly Material[] _original;
        private Material[] _selected;
        internal MeshBinding(SeasonalMesh mesh, SeasonsMaterialsFixer fixer)
        {
            if (!mesh.Renderer) throw new InvalidOperationException("Missing seasonal mesh renderer: " + mesh.SourceKey);
            _mesh = mesh;
            _fixer = fixer;
            _original = mesh.Renderer.sharedMaterials;
            _selected = mesh.ForSeason((int)MicroSplatObject.currentSeason);
        }
        private Task Select(int season) { _selected = _mesh.ForSeason(season); return Task.CompletedTask; }
        public Task LoadSummer() => Select(0);
        public Task LoadAutumn() => Select(1);
        public Task LoadWinter() => Select(2);
        public Task LoadSpring() => Select(3);
        public Task LoadAutumnLate() => Select(4);
        public Task LoadSpringEarly() => Select(5);
        public void Fix()
        {
            if (_mesh && _mesh.Renderer) _mesh.Renderer.sharedMaterials = _selected;
        }
        public void Unload()
        {
            if (_mesh && _mesh.Renderer) _mesh.Renderer.sharedMaterials = _original;
        }
        public void Dispose() { if (_fixer) _fixer.Remove(this); Unload(); }
    }

    internal static void BeforeTerrainSeason(MicroSplatTerrain terrain)
    {
        if (!terrain.TryGetComponent<TerrainSeasons>(out var palette) || palette.Terrain != terrain) return;
        var selected = palette.Get((int)MicroSplatObject.currentSeason, (int)MicroSplatObject.currentQuality);
        if (selected.Keywords is not MicroSplatKeywords keywords || selected.Properties is not MicroSplatPropData properties)
            throw new InvalidOperationException("Terrain seasonal settings have the wrong runtime type: " + palette.SourceKey);
        terrain.keywordSO = keywords;
        terrain.propData = properties;
        // SPT discards unused seasonal references after Awake. Refill from the
        // retained authored palette before native selection, including repeats.
        terrain.templateMaterialHigh = Materials(palette, 0);
        terrain.templateMaterialNormal = Materials(palette, 1);
        terrain.templateMaterialLow = Materials(palette, 2);
    }

    private static MicroSplatObject.MaterialsSet Materials(TerrainSeasons palette, int quality) => new()
    {
        SummerMaterial = palette.Get(0, quality).Material,
        AutumnMaterial = palette.Get(1, quality).Material,
        WinterMaterial = palette.Get(2, quality).Material,
        SpringMaterial = palette.Get(3, quality).Material,
        AutumnLateMaterial = palette.Get(4, quality).Material,
        SpringEarlyMaterial = palette.Get(5, quality).Material
    };
}

internal sealed class TerrainSeasonPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(MicroSplatTerrain), "ApplySeasonMaterial", Type.EmptyTypes);
    [PatchPrefix]
    private static void Prefix(MicroSplatTerrain __instance) => SeasonBindings.BeforeTerrainSeason(__instance);
}
