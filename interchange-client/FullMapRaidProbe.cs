using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Comfort.Common;
using Cysharp.Threading.Tasks;
using EFT;
using Manimal.Interchange.Shared;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace Manimal.Interchange.Client;

internal static class FullMapRaidProbe
{
    private sealed class SceneResult
    {
        public string Path = "";
        public int Roots, Objects, Components, MissingScripts, Renderers, ActiveRenderers, Colliders,
            ActiveColliders, Terrains, EmptyMeshColliders, EmptyMeshFilters, MissingMaterialSlots, InvalidActiveMaterials;
        public readonly Dictionary<string, int> ComponentTypes = new(StringComparer.Ordinal);
    }

    internal static async UniTask<object> Run(CancellationToken token)
    {
        if (!Singleton<GameWorld>.Instantiated || !Singleton<GameWorld>.Instance.MainPlayer)
            throw new InvalidOperationException("Full map observation requires an active raid and local player");
        var manifestPath = Path.Combine(Plugin.Root, ManifestRules.FileName);
        var loadedHash = SceneLoader.LoadedManifestSha256;
        var manifest = SceneLoader.ReadManifest(out var snapshotHash);
        if (loadedHash.Length != 64 || snapshotHash != loadedHash)
            throw new InvalidDataException("On-disk manifest differs from the content loaded for this raid");
        if (manifest.Mode != "test" && manifest.Mode != "rework")
            throw new InvalidDataException("Full map observation requires replacement content");
        var results = new List<SceneResult>();
        var failures = 0;
        var examples = new List<string>();
        var stack = new Stack<Transform>();
        var components = new List<Component>();
        var materials = new List<Material>();
        var timer = Stopwatch.StartNew();
        void Fail(string detail)
        {
            failures++;
            if (examples.Count < 80) examples.Add(detail);
        }
        foreach (var mapping in manifest.Scenes)
        {
            token.ThrowIfCancellationRequested();
            var scene = SceneManager.GetSceneByPath(mapping.ReplacementPath);
            if (!SceneLoader.Owns(mapping.ReplacementPath) || !scene.IsValid() || !scene.isLoaded)
            {
                Fail("Required replacement scene is not loaded: " + mapping.ReplacementPath);
                continue;
            }
            var row = new SceneResult { Path = scene.path, Roots = scene.rootCount };
            foreach (var root in scene.GetRootGameObjects()) stack.Push(root.transform);
            while (stack.Count != 0)
            {
                token.ThrowIfCancellationRequested();
                var transform = stack.Pop();
                if (!transform) { Fail("Object was destroyed during scene observation: " + row.Path); continue; }
                for (var i = 0; i < transform.childCount; i++) stack.Push(transform.GetChild(i));
                row.Objects++;
                components.Clear();
                transform.gameObject.GetComponents(components);
                foreach (var component in components)
                {
                    if (!component)
                    {
                        row.MissingScripts++;
                        Fail("Missing component: " + row.Path + " / " + transform.name);
                        continue;
                    }
                    row.Components++;
                    var name = component.GetType().FullName!;
                    row.ComponentTypes.TryGetValue(name, out var count);
                    row.ComponentTypes[name] = count + 1;
                    if (component is Renderer renderer)
                    {
                        row.Renderers++;
                        var active = renderer.enabled && renderer.gameObject.activeInHierarchy;
                        if (active) row.ActiveRenderers++;
                        materials.Clear();
                        renderer.GetSharedMaterials(materials);
                        foreach (var material in materials)
                        {
                            // Empty slots can be deliberately populated by native lifecycle code.
                            if (!material) { row.MissingMaterialSlots++; continue; }
                            var shader = material.shader;
                            if (active && (!shader || !shader.isSupported || shader.name == "Hidden/InternalErrorShader"))
                            {
                                row.InvalidActiveMaterials++;
                                Fail("Invalid active material: " + row.Path + " / " + transform.name + " / " + material.name);
                            }
                        }
                    }
                    if (component is Collider collider)
                    {
                        row.Colliders++;
                        if (collider.enabled && collider.gameObject.activeInHierarchy) row.ActiveColliders++;
                        if (collider is MeshCollider meshCollider && !meshCollider.sharedMesh) row.EmptyMeshColliders++;
                        if (collider is TerrainCollider terrainCollider && terrainCollider.enabled &&
                            terrainCollider.gameObject.activeInHierarchy && !terrainCollider.terrainData)
                            Fail("Active terrain collider has no terrain data: " + row.Path + " / " + transform.name);
                    }
                    if (component is MeshFilter filter && !filter.sharedMesh) row.EmptyMeshFilters++;
                    if (component is Terrain terrain)
                    {
                        row.Terrains++;
                        if (terrain.isActiveAndEnabled && !terrain.terrainData)
                            Fail("Active terrain has no terrain data: " + row.Path + " / " + transform.name);
                    }
                }
                if (timer.ElapsedMilliseconds >= 6)
                {
                    await UniTask.Yield(cancellationToken: token);
                    timer.Restart();
                }
            }
            results.Add(row);
        }
        if (!Singleton<GameWorld>.Instantiated || !Singleton<GameWorld>.Instance.MainPlayer)
            throw new InvalidOperationException("Raid ended during full map observation");
        var position = Singleton<GameWorld>.Instance.MainPlayer.Transform.position;
        var groundFound = Physics.Raycast(position + Vector3.up * 2, Vector3.down, out var ground, 200,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        var navFound = NavMesh.SamplePosition(position, out var nav, 50, NavMesh.AllAreas);
        if (SceneLoader.LoadedManifestSha256 != loadedHash || ManifestRules.Hash(manifestPath) != loadedHash)
            throw new InvalidDataException("Loaded content identity changed during scene observation");
        return new
        {
            Passed = failures == 0, ManifestSha256 = loadedHash, manifest.ContentId,
            Scope = "Read-only live scene/component/material census. Ground and navigation are observations at the current player position; this does not prove map-wide gameplay, visibility, AI, loot or extraction behavior.",
            Scenes = results, FailureCount = failures, FailureExamples = examples,
            PlayerPosition = new { position.x, position.y, position.z },
            Ground = new { Found = groundFound, Distance = groundFound ? (float?)ground.distance : null,
                Scene = groundFound ? ground.collider.gameObject.scene.path : null },
            Navigation = new { Found = navFound, Distance = navFound ? (float?)nav.distance : null }
        };
    }
}
