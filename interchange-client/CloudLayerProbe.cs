using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using EFT;
using HarmonyLib;
using Manimal.Interchange.Components;
using Manimal.Interchange.Shared;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using ZLinq;

namespace Manimal.Interchange.Client;

internal static class CloudLayerProbe
{
    private sealed class Input { public string Bundle = ""; public string Sha256 = ""; public string Scene = ""; }
    internal static async UniTask<object> Run(CancellationToken token)
    {
        var input = JsonConvert.DeserializeObject<Input>(File.ReadAllText(Path.Combine(Plugin.Root, "cloud-validation.json")))
            ?? throw new InvalidDataException("Cloud fixture manifest missing");
        var path = ManifestRules.Resolve(Plugin.Root, input.Bundle);
        if (ManifestRules.Hash(path) != input.Sha256) throw new InvalidDataException("Cloud fixture changed");
        var beforeScenes = SceneManager.sceneCount;
        int Callbacks() => Camera.onPreRender?.GetInvocationList().Length ?? 0;
        var beforeCallbacks = Callbacks();
        var globalStretch = Shader.GetGlobalVector(CloudLayerPatch.Stretch);
        var planetId = Shader.PropertyToID("_PlanetCenterRadius");
        var exposureId = Shader.PropertyToID("_ExposureMultiplier");
        var globalPlanet = Shader.GetGlobalVector(planetId);
        var globalExposure = Shader.GetGlobalFloat(exposureId);
        var checks = new List<string>();
        var errors = new List<string>();
        void Check(bool pass, string name) { if (!pass) throw new InvalidDataException(name); checks.Add(name); }
        void Error(string message, string stack, LogType kind)
        { if (kind is LogType.Error or LogType.Exception or LogType.Assert) errors.Add(message); }
        var settingsType = typeof(GameWorld).Assembly.GetType("EFT.Rendering.Clouds.CloudLayer", true);
        var parametersType = typeof(GameWorld).Assembly.GetType("EFT.Rendering.Clouds.BuiltinSkyParameters", true);
        var settingsField = AccessTools.Field(CloudLayerPatch.ControllerType, "_settings");
        AssetBundle? bundle = null;
        Scene scene = default;
        GameObject? root = null;
        ScriptableObject? settings = null;
        RenderTexture? renderTarget = null;
        Texture2D? readback = null;
        var materials = new List<Material>();
        Application.logMessageReceived += Error;
        try
        {
            token.ThrowIfCancellationRequested();
            var request = AssetBundle.LoadFromFileAsync(path);
            await UniTask.WaitUntil(() => request.isDone);
            bundle = request.assetBundle;
            if (!bundle) throw new InvalidDataException("Cloud fixture failed to load");
            Check(bundle.GetAllScenePaths().AsValueEnumerable().SequenceEqual(new[] { input.Scene }), "Exact cloud fixture scene");
            var load = SceneManager.LoadSceneAsync(input.Scene, LoadSceneMode.Additive);
            await UniTask.WaitUntil(() => load.isDone);
            scene = SceneManager.GetSceneByPath(input.Scene);
            var roots = scene.GetRootGameObjects();
            Check(roots.Length == 1 && !roots[0].activeSelf, "Fixture remains inactive until configured");
            root = roots[0];
            var policy = root.GetComponentsInChildren<CloudLayerPolicy>(true).AsValueEnumerable().Single();
            var controllers = root.GetComponentsInChildren(CloudLayerPatch.ControllerType, true);
            Check(controllers.Length == 2, "Two actual target cloud controllers deserialized");
            Check(policy.PixelShader && policy.PixelShader.name == "Hidden/HDRP/Sky/CloudLayer" && policy.PixelShader.isSupported, "Complete retail shader loads and supports the target GPU");
            Check(policy.StretchDistance == .08f, "Exact authored stretch survives serialization");
            settings = ScriptableObject.CreateInstance(settingsType);
            AccessTools.Field(settingsType, "Resolution").SetValue(settings, Enum.ToObject(AccessTools.Field(settingsType, "Resolution").FieldType, 256));
            AccessTools.Method(settingsType, "Init").Invoke(settings, null);
            foreach (var controller in controllers) settingsField.SetValue(controller, settings);
            renderTarget = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            Check(renderTarget.Create(), "Isolated GPU render target allocated");
            readback = new Texture2D(32, 32, TextureFormat.RGBA32, false);
            for (var cycle = 0; cycle < 2; cycle++)
            {
                root.SetActive(true);
                // Complete native setup/render/disable in one frame so the fixture
                // never receives a callback from a live menu camera.
                try
                {
                    Check(Callbacks() == beforeCallbacks + 2, "Native camera callbacks registered: " + cycle);
                    var renderer = CloudLayerPatch.Renderer.GetValue(policy.Controller);
                    var material = (Material)CloudLayerPatch.Material.GetValue(renderer);
                    materials.Add(material);
                    Check(material.shader == policy.PixelShader && material.renderQueue == 3000, "Owned material uses retail shader and native queue: " + cycle);
                    var block = (MaterialPropertyBlock)CloudLayerPatch.PropertyBlock.GetValue(renderer);
                    Check(block.GetVector(CloudLayerPatch.Stretch) == new Vector4(.08f, 0, 0, 0), "Native draw block contains exact retail stretch: " + cycle);
                    var control = controllers.AsValueEnumerable().Single(c => c != policy.Controller);
                    var controlRenderer = CloudLayerPatch.Renderer.GetValue(control);
                    var controlMaterial = (Material)CloudLayerPatch.Material.GetValue(controlRenderer);
                    materials.Add(controlMaterial);
                    Check(controlMaterial.shader != policy.PixelShader && controlMaterial.shader.name == policy.PixelShader.name, "Unowned controller retains its native SPT shader: " + cycle);
                    Check(Shader.GetGlobalVector(CloudLayerPatch.Stretch) == globalStretch, "Global cloud stretch unchanged: " + cycle);
                    using var buffer = new CommandBuffer { name = "MI Cloud GPU validation" };
                    var parameters = Activator.CreateInstance(parametersType);
                    void Set(string name, object value) => AccessTools.Field(parametersType, name).SetValue(parameters, value);
                    Set("CloudSettings", settings); Set("CommandBuffer", buffer); Set("FrameIndex", Time.frameCount + cycle + 1);
                    Set("ColorBuffer", new RenderTargetIdentifier(renderTarget)); Set("ExposureMultiplier", 1f);
                    Set("ScreenSize", new Vector4(32, 32, 1f / 32, 1f / 32)); Set("CubemapFace", CubemapFace.Unknown);
                    var matrix = Matrix4x4.identity;
                    matrix.SetRow(0, new Vector4(.04f, 0, -.64f, 0));
                    matrix.SetRow(1, new Vector4(0, .01f, .5f, 0));
                    matrix.SetRow(2, new Vector4(0, 0, 1, 0));
                    Set("PixelCoordToViewDirMatrix", matrix);
                    var ambient = AccessTools.Field(CloudLayerPatch.ControllerType, "_ambientBuffer").GetValue(policy.Controller);
                    var ambientBuffer = (ComputeBuffer)AccessTools.Field(ambient.GetType(), "_buffer").GetValue(ambient);
                    ambientBuffer.SetData(new Vector4[7]);
                    Set("CloudAmbientProbe", ambientBuffer);
                    AccessTools.Method(CloudLayerPatch.RendererType, "DoUpdate").Invoke(renderer, new[] { parameters });
                    AccessTools.Method(CloudLayerPatch.RendererType, "UpdateTexture", new[] { parametersType }).Invoke(renderer, new[] { parameters });
                    buffer.SetRenderTarget(renderTarget);
                    buffer.ClearRenderTarget(false, true, Color.magenta);
                    AccessTools.Method(CloudLayerPatch.RendererType, "RenderClouds").Invoke(renderer, new[] { parameters, (object)true });
                    Graphics.ExecuteCommandBuffer(buffer);
                    var previous = RenderTexture.active;
                    try
                    {
                        RenderTexture.active = renderTarget;
                        readback.ReadPixels(new Rect(0, 0, 32, 32), 0, 0);
                        readback.Apply();
                    }
                    finally { RenderTexture.active = previous; }
                    Check(readback.GetPixels32().AsValueEnumerable().Any(p => p.r != 255 || p.g != 0 || p.b != 255), "Native compute and retail pixel shader produce GPU output: " + cycle);
                    Check(block.GetVector(CloudLayerPatch.Stretch) == new Vector4(.08f, 0, 0, 0), "Native render preserves per-renderer stretch: " + cycle);
                }
                finally { root.SetActive(false); }
                Check(Callbacks() == beforeCallbacks, "Native camera callbacks removed: " + cycle);
                Check(controllers.AsValueEnumerable().All(c => CloudLayerPatch.Renderer.GetValue(c) == null), "Native renderer ownership cleared: " + cycle);
                await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
                Check(materials.AsValueEnumerable().All(m => !m), "Native materials destroyed after disable: " + cycle);
            }
        }
        finally
        {
            if (root != null && root) root.SetActive(false);
            if (scene.IsValid() && scene.isLoaded)
            {
                var unload = SceneManager.UnloadSceneAsync(scene);
                if (unload != null) await UniTask.WaitUntil(() => unload.isDone);
            }
            if (bundle != null) bundle.Unload(true);
            if (settings) UnityEngine.Object.Destroy(settings);
            if (readback) UnityEngine.Object.Destroy(readback);
            if (renderTarget != null && renderTarget) { renderTarget.Release(); UnityEngine.Object.Destroy(renderTarget); }
            Shader.SetGlobalVector(planetId, globalPlanet);
            Shader.SetGlobalFloat(exposureId, globalExposure);
            Application.logMessageReceived -= Error;
        }
        Check(Callbacks() == beforeCallbacks && SceneManager.sceneCount == beforeScenes, "Original menu scenes and camera callbacks restored");
        Check(Shader.GetGlobalVector(CloudLayerPatch.Stretch) == globalStretch, "Global cloud stretch preserved after cleanup");
        Check(errors.Count == 0, "No Unity errors: " + string.Join("; ", errors));
        return new { Passed = true, BundleSha256 = input.Sha256, CheckCount = checks.Count, Checks = checks, UnityErrors = errors,
            Scope = "Native controller lifecycle, exact compiled retail shader and stretch, real target compute and GPU render, unowned control, repeated enables and cleanup. Full-map weather appearance remains a raid test." };
    }
}
