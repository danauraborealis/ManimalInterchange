using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using JBooth.MicroSplat;
using Manimal.Interchange.Components;
using Manimal.Interchange.Shared;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZLinq;

namespace Manimal.Interchange.Client;

internal static class SeasonProbe
{
    private sealed class Input { public string Bundle = ""; public string Sha256 = ""; public string Scene = ""; }

    internal static async UniTask<object> Run(CancellationToken token)
    {
        var input = JsonConvert.DeserializeObject<Input>(File.ReadAllText(Path.Combine(Plugin.Root, "season-validation.json")))
            ?? throw new InvalidDataException("Season fixture manifest missing");
        var path = ManifestRules.Resolve(Plugin.Root, input.Bundle);
        if (ManifestRules.Hash(path) != input.Sha256) throw new InvalidDataException("Season fixture changed");
        var beforeScenes = SceneManager.sceneCount;
        var beforeBindings = SeasonBindings.RegisteredMeshes;
        var originalSeason = MicroSplatObject.currentSeason;
        var originalQuality = MicroSplatObject.currentQuality;
        var errors = new List<string>();
        void Error(string message, string stack, LogType type)
        { if (type is LogType.Error or LogType.Exception or LogType.Assert) errors.Add(message); }
        var checks = new List<string>();
        void Check(bool pass, string name)
        { if (!pass) throw new InvalidDataException(name); checks.Add(name); }
        AssetBundle? bundle = null;
        Scene scene = default;
        IDisposable? scope = null;
        SeasonsMaterialsGroup? group = null;
        SeasonsMaterialsMap? map = null;
        SeasonalMesh[] meshes = Array.Empty<SeasonalMesh>();
        TerrainSeasons[] terrains = Array.Empty<TerrainSeasons>();
        Application.logMessageReceived += Error;
        try
        {
            token.ThrowIfCancellationRequested();
            var request = AssetBundle.LoadFromFileAsync(path);
            await UniTask.WaitUntil(() => request.isDone);
            bundle = request.assetBundle;
            if (!bundle) throw new InvalidDataException("Season fixture failed to load");
            Check(bundle.GetAllScenePaths().AsValueEnumerable().SequenceEqual(new[] { input.Scene }), "Exact seasonal scene path");
            var load = SceneManager.LoadSceneAsync(input.Scene, LoadSceneMode.Additive);
            await UniTask.WaitUntil(() => load.isDone);
            scene = SceneManager.GetSceneByPath(input.Scene);
            var roots = scene.GetRootGameObjects();
            Check(roots.Length == 1 && !roots[0].activeSelf, "Fixture begins inactive");
            var root = roots[0];
            meshes = root.GetComponentsInChildren<SeasonalMesh>(true);
            terrains = root.GetComponentsInChildren<TerrainSeasons>(true);
            Check(meshes.Length == 8 && terrains.Length == 4, "All mesh and terrain components deserialized");
            var controller = new GameObject("MI_IsolatedSeasonController");
            controller.SetActive(false);
            SceneManager.MoveGameObjectToScene(controller, scene);
            controller.transform.SetParent(root.transform, false);
            // Invoke the real controller API on an inactive, isolated instance.
            // Its singleton Awake never runs, so menu controller state is safe.
            var fixer = controller.AddComponent<SeasonsMaterialsFixer>();
            group = ScriptableObject.CreateInstance<SeasonsMaterialsGroup>();
            group.Materials = new List<ISeasonsMaterial>();
            map = ScriptableObject.CreateInstance<SeasonsMaterialsMap>();
            map.Groups = Array.Empty<SeasonsMaterialsGroup>();
            AccessTools.Field(typeof(SeasonsMaterialsFixer), "_runtimeGroup").SetValue(fixer, group);
            AccessTools.Field(typeof(SeasonsMaterialsFixer), "_materialsMap").SetValue(fixer, map);
            scope = SeasonBindings.ClaimProbeFixer(scene.handle, fixer);
            var originals = new Dictionary<SeasonalMesh, Material[]>();
            foreach (var mesh in meshes) originals.Add(mesh, mesh.Renderer.sharedMaterials);
            root.SetActive(true);
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
            Check(SeasonBindings.RegisteredMeshes == beforeBindings + 8 && group.Materials.Count == 8, "Start registers each mesh once in the native controller");
            var seasonLoads = new Func<Task>[] { fixer.LoadSummer, fixer.LoadAutumn, fixer.LoadWinter,
                fixer.LoadSpring, fixer.LoadAutumnLate, fixer.LoadSpringEarly };
            for (var season = 0; season < 6; season++)
            {
                token.ThrowIfCancellationRequested();
                await seasonLoads[season]().AsUniTask();
                group.Fix();
                group.Fix();
                foreach (var mesh in meshes)
                    Check(mesh.Renderer.sharedMaterials.AsValueEnumerable().SequenceEqual(mesh.ForSeason(season)),
                        "Native season " + season + " assigns exact mesh material: " + mesh.SourceKey);
                fixer.Unload();
                foreach (var mesh in meshes)
                    Check(mesh.Renderer.sharedMaterials.AsValueEnumerable().SequenceEqual(originals[mesh]),
                        "Native unload restores initial materials after repeated Fix: " + mesh.SourceKey + "/" + season);
                MicroSplatObject.currentSeason = (TextureArrayConfig.Season)season;
                for (var quality = 0; quality < 3; quality++)
                {
                    MicroSplatObject.currentQuality = (MicroSplatObject.QUALITY)quality;
                    foreach (var palette in terrains)
                    {
                        var terrain = (MicroSplatTerrain)palette.Terrain;
                        var wanted = palette.Get(season, quality);
                        terrain.ApplySeasonMaterial();
                        Check(terrain.templateMaterial == wanted.Material && terrain.keywordSO == wanted.Keywords && terrain.propData == wanted.Properties,
                            "Native terrain selection preserves material/keywords/properties: " + palette.SourceKey + "/" + season + "/" + quality);
                        terrain.UnloadUnusedSeasonMaterials();
                        terrain.ApplySeasonMaterial();
                        Check(terrain.templateMaterial == wanted.Material && terrain.keywordSO == wanted.Keywords && terrain.propData == wanted.Properties,
                            "Terrain palette survives native reference cleanup: " + palette.SourceKey + "/" + season + "/" + quality);
                    }
                }
            }
            UnityEngine.Object.Destroy(meshes[0].gameObject);
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
            Check(SeasonBindings.RegisteredMeshes == beforeBindings + 7 && group.Materials.Count == 7, "Destroy unregisters native seasonal membership");
            await fixer.LoadWinter().AsUniTask();
            fixer.FixMaterials();
            foreach (var mesh in meshes)
                if (mesh) Check(mesh.Renderer.sharedMaterials.AsValueEnumerable().SequenceEqual(mesh.Winter), "Native final material commit: " + mesh.SourceKey);
            Check(AccessTools.Field(typeof(SeasonsMaterialsFixer), "_runtimeGroup").GetValue(fixer) == null, "Native controller releases staging group after commit");
        }
        finally
        {
            MicroSplatObject.currentSeason = originalSeason;
            MicroSplatObject.currentQuality = originalQuality;
            if (scene.IsValid() && scene.isLoaded)
            {
                var unload = SceneManager.UnloadSceneAsync(scene);
                await UniTask.WaitUntil(() => unload.isDone);
            }
            scope?.Dispose();
            if (group) UnityEngine.Object.Destroy(group);
            if (map) UnityEngine.Object.Destroy(map);
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
            if (bundle != null) bundle.Unload(true);
            Application.logMessageReceived -= Error;
        }
        Check(SeasonBindings.RegisteredMeshes == beforeBindings, "All mesh registrations released on scene unload");
        Check(SceneManager.sceneCount == beforeScenes, "Original menu scene count restored");
        Check(!meshes.AsValueEnumerable().Any(x => x) && !terrains.AsValueEnumerable().Any(x => x), "All fixture components destroyed");
        Check(!group && !map, "Isolated controller assets destroyed");
        Check(errors.Count == 0, "No Unity errors: " + string.Join("; ", errors));
        return new { Passed = true, BundleSha256 = input.Sha256, CheckCount = checks.Count, Checks = checks,
            MeshCount = 8, TerrainCount = 4, Seasons = 6, Qualities = 3, UnityErrors = errors,
            Scope = "Native seasonal controller registration/material assignment/unload and all 18 native terrain selection cases. Full terrain rendering and collision remain map tests." };
    }
}
