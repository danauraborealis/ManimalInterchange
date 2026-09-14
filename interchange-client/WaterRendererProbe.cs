using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Manimal.Interchange.Components;
using Manimal.Interchange.Shared;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using WaterSSR;
using ZLinq;

namespace Manimal.Interchange.Client;

internal static class WaterRendererProbe
{
    private sealed class Input
    {
        public string Bundle = "", Sha256 = "", Scene = "", NativeBundle = "", NativeSha256 = "", DonorFile = "", DonorSha256 = "";
    }
    internal static async UniTask<object> Run(CancellationToken token)
    {
        var input = JsonConvert.DeserializeObject<Input>(File.ReadAllText(Path.Combine(Plugin.Root, "water-validation.json")))!;
        foreach (var file in new[] { (input.Bundle, input.Sha256), (input.NativeBundle, input.NativeSha256) })
            if (ManifestRules.Hash(ManifestRules.Resolve(Plugin.Root, file.Item1)) != file.Item2) throw new InvalidDataException("Water fixture changed");
        if (ManifestRules.Hash(ManifestRules.Resolve(Application.dataPath, input.DonorFile)) != input.DonorSha256) throw new InvalidDataException("Native water donor changed");
        var before = SceneManager.sceneCount; var callbacks = Camera.onPreCull?.GetInvocationList().Length ?? 0;
        var checks = new List<string>(); var errors = new List<string>(); var materials = new List<Material>();
        void Check(bool pass, string name) { if (!pass) throw new InvalidDataException(name); checks.Add(name); }
        void Error(string message, string stack, LogType kind) { if (kind is LogType.Error or LogType.Exception or LogType.Assert) errors.Add(message + "\n" + stack); }
        AssetBundle? bundle = null, nativeBundle = null; Scene scene = default;
        RenderTexture? target = null; Texture2D? readback = null; Shader? shader = null;
        Application.logMessageReceived += Error;
        try
        {
            token.ThrowIfCancellationRequested();
            var nativeRequest = AssetBundle.LoadFromFileAsync(ManifestRules.Resolve(Plugin.Root, input.NativeBundle)); await UniTask.WaitUntil(() => nativeRequest.isDone);
            nativeBundle = nativeRequest.assetBundle; Check(nativeBundle, "Container-only native water reference bundle loaded");
            shader = nativeBundle!.LoadAsset<Shader>(WaterRendererBindings.NativeShaderAssetPath);
            Check(shader && shader!.name == WaterRendererBindings.NativeShaderName && shader.isSupported, "Exact installed water shader resolves and is supported on the GPU");
            var request = AssetBundle.LoadFromFileAsync(ManifestRules.Resolve(Plugin.Root, input.Bundle)); await UniTask.WaitUntil(() => request.isDone);
            bundle = request.assetBundle; Check(bundle, "Native water fixture loaded");
            Check(bundle!.GetAllScenePaths().AsValueEnumerable().SequenceEqual(new[] { input.Scene }), "Exact water scene path");
            var load = SceneManager.LoadSceneAsync(input.Scene, LoadSceneMode.Additive); await UniTask.WaitUntil(() => load.isDone);
            scene = SceneManager.GetSceneByPath(input.Scene); var root = scene.GetRootGameObjects().AsValueEnumerable().Single();
            var policy = root.GetComponentInChildren<WaterRendererPolicy>(true); var owner = (WaterRendererv3)policy.Owner;
            var group = root.GetComponentInChildren<WaterForSSRv3>(true); var camera = root.GetComponentInChildren<Camera>(true);
            Check(!root.activeSelf && !(AccessTools.Field(typeof(WaterRendererv3), "_shader").GetValue(owner) as Shader), "Renderer starts inactive with no serialized retail shader");
            Check(policy.SourceResolution == 4 && !policy.UnderwaterEnabled && !policy.DynamicFoamEnabled && policy.BlurIterations == 4, "Authored inactive water features and resolution evidence retained");
            Check(group.Targets.Length == 4 && group.Targets.AsValueEnumerable().All(t => t != null && t.Filter && t.Renderer && t.GameObject), "All four serialized native water targets resolve");
            Check(group.Targets.AsValueEnumerable().All(t => t.Mesh == group.Targets[0].Mesh), "Shared water mesh identity retained");
            camera.enabled = false; camera.cullingMask = 1 << 31;
            foreach (var surface in group.Targets) surface.GameObject.layer = 31;
            target = new RenderTexture(128, 128, 24, RenderTextureFormat.ARGB32); Check(target.Create(), "Isolated deferred render target allocated");
            camera.targetTexture = target; readback = new Texture2D(128, 128, TextureFormat.RGBA32, false, true);
            var shaderField = AccessTools.Field(typeof(WaterRendererv3), "_shader"); var materialField = AccessTools.Field(typeof(WaterRendererv3), "material_0");
            var targetsField = AccessTools.Field(typeof(WaterRendererv3), "list_1");
            for (var cycle = 0; cycle < 2; cycle++)
            {
                root.SetActive(true); await UniTask.Yield();
                Check(owner.enabled && (shaderField.GetValue(owner) as Shader) == shader, "Native shader bound before OnEnable: " + cycle);
                var material = (Material)materialField.GetValue(owner); materials.Add(material);
                Check(material && material.shader == shader && material.passCount >= 2, "Native water material exposes both draw passes: " + cycle);
                Check((Camera.onPreCull?.GetInvocationList().Length ?? 0) == callbacks + 1, "Exactly one native camera callback registered: " + cycle);
                camera.Render(); await UniTask.Yield(); camera.Render();
                Check(camera.actualRenderingPath == RenderingPath.DeferredShading, "Target camera executed deferred rendering: " + cycle);
                var commands = camera.GetCommandBuffers(CameraEvent.BeforeReflections);
                Check(commands.Length == 1 && commands[0].sizeInBytes > 0, "Native BeforeReflections commands populated: " + cycle);
                var drawn = (List<WaterObject>)targetsField.GetValue(owner);
                Check(drawn.Count == 4 && drawn.AsValueEnumerable().All(t => t.Mesh && t.Mesh.GetNativeVertexBufferPtr(0) != IntPtr.Zero), "Four native water surfaces entered the GPU draw path: " + cycle);
                var previous = RenderTexture.active;
                try { RenderTexture.active = target; readback.ReadPixels(new Rect(0, 0, 128, 128), 0, 0); readback.Apply(false); }
                finally { RenderTexture.active = previous; }
                Check(readback.GetPixels32().Length == 16384 && target.GetNativeTexturePtr() != IntPtr.Zero, "Rendered frame read back from GPU: " + cycle);
                root.SetActive(false); await UniTask.Yield();
                Check((Camera.onPreCull?.GetInvocationList().Length ?? 0) == callbacks && camera.GetCommandBuffers(CameraEvent.BeforeReflections).Length == 0,
                    "Native disable removes camera callback and commands: " + cycle);
                Check(!material, "Native disable destroys its material: " + cycle);
                token.ThrowIfCancellationRequested();
            }
            camera.targetTexture = null;
        }
        catch (Exception error) { throw new InvalidDataException("Water probe stopped after " + (checks.Count == 0 ? "setup" : checks[checks.Count - 1]), error); }
        finally
        {
            if (scene.IsValid() && scene.isLoaded) { var unload = SceneManager.UnloadSceneAsync(scene); await UniTask.WaitUntil(() => unload.isDone); }
            if (bundle) bundle!.Unload(true);
            if (nativeBundle) nativeBundle!.Unload(false);
            if (target) { target!.Release(); UnityEngine.Object.Destroy(target); }
            if (readback) UnityEngine.Object.Destroy(readback);
            await UniTask.Yield(); Application.logMessageReceived -= Error;
        }
        Check(SceneManager.sceneCount == before && (Camera.onPreCull?.GetInvocationList().Length ?? 0) == callbacks, "Scene unload restores native camera and scene inventory");
        Check(materials.AsValueEnumerable().All(m => !m) && shader, "Owned materials destroyed and installed native shader retained");
        Check(errors.Count == 0, "No Unity GPU or lifecycle errors" + (errors.Count == 0 ? "" : ": " + string.Join("\n", errors)));
        return new { passed = true, checks, errors, bundle = input.Sha256, nativeBundle = input.NativeSha256,
            scope = "Native shader resolved without embedded payload, four serialized surfaces, deferred GPU draw path, two enable/disable cycles and clean unload. Full-map appearance and target camera-sized buffer performance remain raid tests." };
    }
}
