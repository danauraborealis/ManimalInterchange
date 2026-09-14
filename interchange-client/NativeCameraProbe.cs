using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Comfort.Common;
using Cysharp.Threading.Tasks;
using Manimal.Interchange.Components;
using Manimal.Interchange.Shared;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZLinq;

namespace Manimal.Interchange.Client;

internal static class NativeCameraProbe
{
    private sealed class Input { public string Bundle = "", Sha256 = "", DonorFile = "", DonorSha256 = ""; }
    internal static async UniTask<object> Run(CancellationToken token)
    {
        var input = JsonConvert.DeserializeObject<Input>(File.ReadAllText(Path.Combine(Plugin.Root, "native-camera-validation.json")))!;
        var path = ManifestRules.Resolve(Plugin.Root, input.Bundle);
        if (ManifestRules.Hash(path) != input.Sha256 || ManifestRules.Hash(ManifestRules.Resolve(Application.dataPath, input.DonorFile)) != input.DonorSha256)
            throw new InvalidDataException("Native camera fixture/donor changed");
        var checks = new List<string>(); var errors = new List<string>();
        var scenes = SceneManager.sceneCount; var callbacks = Camera.onPreCull?.GetInvocationList().Length ?? 0;
        var prior = Singleton<LevelSettings>.Instantiated ? Singleton<LevelSettings>.Instance : null;
        void Check(bool value, string name) { if (!value) throw new InvalidDataException(name); checks.Add(name); }
        void Error(string message, string stack, LogType kind) { if (kind is LogType.Error or LogType.Exception or LogType.Assert) errors.Add(message + "\n" + stack); }
        AssetBundle? bundle = null; Camera? camera = null; GameObject? root = null; var components = 0;
        Application.logMessageReceived += Error;
        try
        {
            token.ThrowIfCancellationRequested();
            var request = AssetBundle.LoadFromFileAsync(path); await UniTask.WaitUntil(() => request.isDone);
            bundle = request.assetBundle; Check(bundle, "Native camera reference bundle loaded");
            camera = NativeCameraBindings.Resolve();
            Check(camera && !camera.gameObject.scene.IsValid(), "Installed camera resolves as an uninstantiated prefab");
            Check(camera!.name == "Cam2_obshaga_zalupa4" && camera.nearClipPlane == .03f && camera.farClipPlane == 3000 && camera.fieldOfView == 75,
                "Authored map camera projection settings match target prefab");
            Check(camera.renderingPath == RenderingPath.DeferredShading && camera.allowHDR && !camera.allowMSAA && !camera.useOcclusionCulling,
                "Authored native camera render flags retained");
            var native = camera.GetComponentsInChildren<Component>(true); components = native.Length;
            Check(components > 50 && native.AsValueEnumerable().All(c => c != null), "Native camera hierarchy has no missing components");
            Check(native.AsValueEnumerable().All(c => !c.GetType().Assembly.GetName().Name!.StartsWith("ManimalInterchange.Authoring", StringComparison.Ordinal)), "All camera scripts bind directly to native runtime assemblies");
            root = new GameObject("MI_NativeCameraProbe"); root.SetActive(false);
            var owner = root.AddComponent<LevelSettings>(); var policy = root.AddComponent<LevelLightingPolicy>();
            policy.NativeLevelSettings = owner; policy.SourceKey = "probe/native-camera";
            Check(!NativeCameraBindings.Apply(owner) && owner.CameraPrefab == null, "Policy opt-in is required before assignment");
            policy.UseNativeCameraPrefab = true; policy.NativeLevelSettings = null!;
            Check(!NativeCameraBindings.Apply(owner) && owner.CameraPrefab == null, "Missing owner reference cannot claim native camera");
            policy.NativeLevelSettings = owner;
            Check(NativeCameraBindings.Apply(owner) && owner.CameraPrefab == camera, "Owned LevelSettings binds the exact installed camera prefab");
            Check(NativeCameraBindings.Apply(owner) && owner.CameraPrefab == camera, "Repeated assignment retains native object identity");
            Check((Singleton<LevelSettings>.Instantiated ? Singleton<LevelSettings>.Instance : null) == prior && (Camera.onPreCull?.GetInvocationList().Length ?? 0) == callbacks,
                "Inactive binding probe leaves camera and LevelSettings lifecycles untouched");
        }
        finally
        {
            if (root) UnityEngine.Object.Destroy(root);
            if (bundle) bundle!.Unload(false);
            await UniTask.Yield(); Application.logMessageReceived -= Error;
        }
        Check(camera && SceneManager.sceneCount == scenes, "Reference container cleanup retains installed native prefab and scene inventory");
        Check((Singleton<LevelSettings>.Instantiated ? Singleton<LevelSettings>.Instance : null) == prior && (Camera.onPreCull?.GetInvocationList().Length ?? 0) == callbacks,
            "Binding fixture cleanup restores native lifecycle inventory");
        Check(errors.Count == 0, "No native camera resolution or cleanup errors" + (errors.Count == 0 ? "" : ": " + string.Join("\n", errors)));
        return new { passed = true, checks, errors, bundle = input.Sha256, nativeComponents = components,
            scope = "Installed camera prefab and supporting runtime scripts resolve with exact authored camera settings; owned LevelSettings assignment and cleanup. Actual player-camera instantiation is exercised by the later raid test." };
    }
}
