using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Manimal.Interchange.Components;
using Manimal.Interchange.Shared;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using ZLinq;

namespace Manimal.Interchange.Client;

internal static class RenderServiceProbe
{
    private sealed class Input { public string Bundle = ""; public string Sha256 = ""; public string Scene = ""; }
    internal static async UniTask<object> Run(CancellationToken token)
    {
        var input = JsonConvert.DeserializeObject<Input>(File.ReadAllText(Path.Combine(Plugin.Root, "render-validation.json")))
            ?? throw new InvalidDataException("Render fixture manifest missing");
        var path = ManifestRules.Resolve(Plugin.Root, input.Bundle);
        if (ManifestRules.Hash(path) != input.Sha256) throw new InvalidDataException("Render fixture changed");
        var beforeScenes = SceneManager.sceneCount;
        var keyword = Shader.IsKeywordEnabled(MountainViewAngle.Keyword);
        var cull = Shader.GetGlobalFloat(MountainViewAngle.CullProperty);
        var feather = Shader.GetGlobalFloat(MountainViewAngle.FeatherProperty);
        var texture = Shader.GetGlobalTexture(SunShadowMap.TextureProperty);
        var checks = new List<string>(); var errors = new List<string>(); var pixels = new List<Color>();
        void Check(bool pass, string name) { if (!pass) throw new InvalidDataException(name); checks.Add(name); }
        bool Near(float a, float b) => Mathf.Abs(a - b) < .00001f;
        void Error(string message, string stack, LogType kind)
        { if (kind is LogType.Error or LogType.Exception or LogType.Assert) errors.Add(message); }
        AssetBundle? bundle = null; Scene scene = default; GameObject? root = null;
        RenderTexture? target = null, captured = null; Texture2D? readback = null;
        Application.logMessageReceived += Error;
        try
        {
            token.ThrowIfCancellationRequested();
            var request = AssetBundle.LoadFromFileAsync(path); await UniTask.WaitUntil(() => request.isDone);
            bundle = request.assetBundle;
            if (!bundle) throw new InvalidDataException("Render fixture failed to load");
            Check(bundle.GetAllScenePaths().AsValueEnumerable().SequenceEqual(new[] { input.Scene }), "Exact fixture scene");
            var load = SceneManager.LoadSceneAsync(input.Scene, LoadSceneMode.Additive); await UniTask.WaitUntil(() => load.isDone);
            scene = SceneManager.GetSceneByPath(input.Scene); var roots = scene.GetRootGameObjects();
            Check(roots.Length == 1 && !roots[0].activeSelf, "Fixture inactive before configuration"); root = roots[0];
            var policy = root.GetComponentsInChildren<MountainViewAngle>(true).AsValueEnumerable().Single(p => p.SourceKey == "probe/mountains");
            var empty = root.GetComponentsInChildren<MountainViewAngle>(true).AsValueEnumerable().Single(p => p.SourceKey == "probe/empty");
            var shadow = root.GetComponentsInChildren<SunShadowMap>(true).AsValueEnumerable().Single();
            var light = shadow.GetComponent<Light>(); var commandsBefore = light.GetCommandBuffers(LightEvent.AfterShadowMap).Length;
            var renderers = policy.GetComponentsInChildren<Renderer>(true);
            Check(renderers.Length == 2 && !renderers[1].gameObject.activeSelf, "Inactive child renderer retained in fixture");
            Check(policy.DirectionAngle == 64 && policy.CullAngle == 110 && policy.FeatherAngle == 8.5f, "Exact retail mountain settings");
            var block = new MaterialPropertyBlock(); var unrelatedId = Shader.PropertyToID("_MI_UnrelatedProbeProperty");
            foreach (var renderer in renderers) { block.Clear(); block.SetFloat(unrelatedId, 123); renderer.SetPropertyBlock(block); }
            using var unrelated = new CommandBuffer { name = "Unowned shadow command control" };
            light.AddCommandBuffer(LightEvent.AfterShadowMap, unrelated);
            target = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Check(target.Create(), "Isolated render target allocated");
            captured = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Check(captured.Create(), "Isolated shadow sample target allocated");
            readback = new Texture2D(32, 32, TextureFormat.RGBA32, false, true);
            for (var cycle = 0; cycle < 2; cycle++)
            {
                token.ThrowIfCancellationRequested();
                root.SetActive(true);
                try
                {
                    Check(!empty.enabled, "Empty mountain controller naturally disables: " + cycle);
                    Check(Shader.IsKeywordEnabled(MountainViewAngle.Keyword), "Native shader keyword enabled: " + cycle);
                    Check(Near(Shader.GetGlobalFloat(MountainViewAngle.CullProperty), .573576436f) &&
                        Near(Shader.GetGlobalFloat(MountainViewAngle.FeatherProperty), .446197813f), "Exact authored cosine globals: " + cycle);
                    renderers[0].GetPropertyBlock(block); var a = block.GetVector(MountainViewAngle.DirectionProperty);
                    renderers[1].GetPropertyBlock(block); var b = block.GetVector(MountainViewAngle.DirectionProperty);
                    Check(Near(a.x, .438371147f) && Near(a.y, .898794046f), "World direction transformed to first renderer: " + cycle);
                    Check(Near(b.x, -.898794046f) && Near(b.y, .438371147f), "Rotated inactive renderer receives local direction: " + cycle);
                    Check(block.GetFloat(unrelatedId) == 123, "Unrelated renderer property retained: " + cycle);
                    var commands = light.GetCommandBuffers(LightEvent.AfterShadowMap);
                    Check(commands.Length == commandsBefore + 2 && commands.AsValueEnumerable().Any(c => c.name == unrelated.name),
                        "One owned native shadow command added: " + cycle + " [" + string.Join(", ", commands.AsValueEnumerable().Select(c => c.name).ToArray()) + "]");
                    using (var setup = new CommandBuffer())
                    {
                        setup.SetRenderTarget(target); setup.ClearRenderTarget(false, true, Color.red);
                        Graphics.ExecuteCommandBuffer(setup);
                    }
                    var extraction = commands.AsValueEnumerable().Single(c => c.name == "Manimal Interchange sun shadow map");
                    var previousTarget = RenderTexture.active;
                    try { RenderTexture.active = target; Graphics.ExecuteCommandBuffer(extraction); }
                    finally { RenderTexture.active = previousTarget; }
                    using (var sample = new CommandBuffer())
                    {
                        sample.SetRenderTarget(captured); sample.ClearRenderTarget(false, true, Color.blue);
                        sample.DrawRenderer(renderers[0], renderers[0].sharedMaterial, 0, 1);
                        Graphics.ExecuteCommandBuffer(sample);
                    }
                    previousTarget = RenderTexture.active;
                    try { RenderTexture.active = captured; readback.ReadPixels(new Rect(0, 0, 32, 32), 0, 0); readback.Apply(); }
                    finally { RenderTexture.active = previousTarget; }
                    var shadowPixel = readback.GetPixel(16, 16);
                    Check(shadowPixel.r > .95f && shadowPixel.g < .05f && shadowPixel.b < .05f,
                        "GPU samples native CurrentActive capture under exact retail texture ID: " + cycle + " " + shadowPixel);
                    using (var draw = new CommandBuffer())
                    {
                        draw.SetRenderTarget(target); draw.DrawRenderer(renderers[0], renderers[0].sharedMaterial, 0, 0);
                        Graphics.ExecuteCommandBuffer(draw);
                    }
                    var previous = RenderTexture.active;
                    try { RenderTexture.active = target; readback.ReadPixels(new Rect(0, 0, 32, 32), 0, 0); readback.Apply(); }
                    finally { RenderTexture.active = previous; }
                    var pixel = readback.GetPixel(16, 16); pixels.Add(pixel);
                    Check(pixel.g > pixel.r + .08f && pixel.r > pixel.b + .06f && pixel.a > .99f,
                        "GPU consumes per-renderer direction, keyword and global cull: " + cycle + " " + pixel);
                    policy.CullAngle = 720; policy.Refresh();
                    Check(Near(Shader.GetGlobalFloat(MountainViewAngle.FeatherProperty), -.999995065f), "Retail feather clamp preserved: " + cycle);
                    policy.CullAngle = 110; policy.Refresh();
                    var rotation = renderers[0].transform.rotation;
                    try
                    {
                        renderers[0].transform.rotation = Quaternion.Euler(0, 0, 90);
                        Check(MountainViewAngle.LocalDirection(renderers[0].transform, 0) == Vector2.right, "Degenerate projected direction uses right-vector fallback: " + cycle);
                    }
                    finally { renderers[0].transform.rotation = rotation; }
                }
                finally { root.SetActive(false); }
                Check(light.GetCommandBuffers(LightEvent.AfterShadowMap).Length == commandsBefore + 1 &&
                    light.GetCommandBuffers(LightEvent.AfterShadowMap).AsValueEnumerable().Any(c => c.name == unrelated.name), "Only owned light command removed: " + cycle);
                Check(Shader.IsKeywordEnabled(MountainViewAngle.Keyword) == keyword &&
                    Shader.GetGlobalFloat(MountainViewAngle.CullProperty) == cull && Shader.GetGlobalFloat(MountainViewAngle.FeatherProperty) == feather,
                    "Previous mountain shader state restored: " + cycle);
                Check(Shader.GetGlobalTexture(SunShadowMap.TextureProperty) == texture, "Previous shadow texture restored: " + cycle);
                foreach (var renderer in renderers)
                {
                    renderer.GetPropertyBlock(block);
                    Check(block.GetVector(MountainViewAngle.DirectionProperty) == Vector4.zero && block.GetFloat(unrelatedId) == 123,
                        "Owned direction restored and unrelated property retained: " + cycle + " " + renderer.name);
                }
                await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
            }
            light.RemoveCommandBuffer(LightEvent.AfterShadowMap, unrelated);
        }
        finally
        {
            if (root != null && root) root.SetActive(false);
            if (scene.IsValid() && scene.isLoaded) { var unload = SceneManager.UnloadSceneAsync(scene); if (unload != null) await UniTask.WaitUntil(() => unload.isDone); }
            if (bundle != null) bundle.Unload(true);
            if (readback) UnityEngine.Object.Destroy(readback);
            if (target != null && target) { target.Release(); UnityEngine.Object.Destroy(target); }
            if (captured != null && captured) { captured.Release(); UnityEngine.Object.Destroy(captured); }
            Application.logMessageReceived -= Error;
        }
        Check(SceneManager.sceneCount == beforeScenes, "Original scene count restored");
        Check(errors.Count == 0, "No Unity errors: " + string.Join("; ", errors));
        return new { Passed = true, BundleSha256 = input.Sha256, CheckCount = checks.Count, Checks = checks, UnityErrors = errors,
            GpuPixels = pixels.AsValueEnumerable().Select(p => new[] { p.r, p.g, p.b, p.a }).ToArray(),
            Scope = "Exact bundled mountain settings, inactive and rotated renderers, live shader globals and GPU output, native shadow command execution and light registration, repeat enable and owned cleanup. Full-map sun/shadow appearance remains a raid test." };
    }
}
