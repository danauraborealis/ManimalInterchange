using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Manimal.Interchange.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZLinq;

namespace Manimal.Interchange.Client;

internal static class AreaLightProbe
{
    private sealed class Sample { public string Key = ""; public Dictionary<string, JToken> Fields = new(); }
    private sealed class Input
    {
        public Sample[] Samples = Array.Empty<Sample>();
        public string LifecycleBundle = "";
        public string LifecycleSha256 = "";
        public string LifecycleScene = "";
    }
    private static readonly string[] FieldNames = { "m_Intensity", "size", "length", "depth", "m_ClipBoxSize", "m_Angle", "m_Hardness",
        "m_SpecularScale", "m_Color", "m_Ambient", "m_Negative", "m_Specular", "ShadowFeather", "InvertedShadowFeather", "m_Spot", "m_SourceColor" };

    internal static async UniTask<object> Run(CancellationToken token)
    {
        var file = Path.Combine(Plugin.Root, "area-light-validation.json");
        var input = JsonConvert.DeserializeObject<Input>(File.ReadAllText(file)) ?? throw new InvalidDataException("Area-light fixture missing");
        if (input.Samples.Length != 1620 || input.Samples.AsValueEnumerable().Select(s => s.Key).Distinct().Count() != 1620)
            throw new InvalidDataException("Area-light source census differs");
        var fields = FieldNames.AsValueEnumerable().ToDictionary(name => name, name => AccessTools.Field(typeof(AreaLight), name));
        var init = AccessTools.Method(typeof(AreaLight), "Init", Type.EmptyTypes);
        var meshField = AccessTools.Field(typeof(AreaLight), "m_SourceMesh");
        var proxyField = AccessTools.Field(typeof(AreaLight), "m_ProxyMaterial");
        var scene = SceneManager.CreateScene("MI_AreaLightValidation");
        var temporary = new List<UnityEngine.Object>();
        AreaLightBindings.Scope? scope = null;
        var passed = 0;
        var nativeControl = false;
        var shaderNames = new List<string>();
        try
        {
            var cubeObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cubeObject.SetActive(false);
            SceneManager.MoveGameObjectToScene(cubeObject, scene);
            temporary.Add(cubeObject);
            var cube = cubeObject.GetComponent<MeshFilter>().sharedMesh;
            var quadObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quadObject.SetActive(false);
            SceneManager.MoveGameObjectToScene(quadObject, scene);
            temporary.Add(quadObject);
            var quad = quadObject.GetComponent<MeshFilter>().sharedMesh;
            foreach (var sample in input.Samples)
            {
                token.ThrowIfCancellationRequested();
                if (sample.Fields.Count != FieldNames.Length || sample.Fields.Keys.AsValueEnumerable().Any(name => !fields.ContainsKey(name)))
                    throw new InvalidDataException("Unexpected area-light test fields");
                var go = new GameObject("MI_AreaLight_" + sample.Key);
                go.SetActive(false);
                SceneManager.MoveGameObjectToScene(go, scene);
                temporary.Add(go);
                var light = go.AddComponent<AreaLight>();
                foreach (var entry in sample.Fields)
                {
                    var field = fields[entry.Key];
                    field.SetValue(light, entry.Value.ToObject(field.FieldType));
                }
                light.m_Quad = quad;
                light.m_Cube = cube;
                light.m_RenderSource = false;
                light.m_Shadows = false;
                if (scope == null)
                {
                    var beforeRenderer = go.GetComponent<MeshRenderer>();
                    nativeControl = !(bool)init.Invoke(light, null)! && light.m_ProxyShader == null && go.GetComponent<MeshRenderer>() == beforeRenderer;
                    if (!nativeControl) throw new InvalidOperationException("Unowned native light control changed");
                    scope = AreaLightBindings.ClaimScene(scene);
                    shaderNames.Add(scope.Proxy.name);
                    shaderNames.Add(scope.Shadow.name);
                    shaderNames.Add(scope.Blur.name);
                }
                if (!(bool)init.Invoke(light, null)!) throw new InvalidOperationException("Native area-light Init failed: " + sample.Key);
                var ownedMesh = (Mesh)meshField.GetValue(light);
                var ownedMaterial = (Material)proxyField.GetValue(light);
                temporary.Add(ownedMesh);
                temporary.Add(ownedMaterial);
                if (ownedMesh == null || ownedMesh == quad || ownedMaterial == null || ownedMaterial.shader != scope.Proxy ||
                    go.GetComponent<MeshFilter>().sharedMesh != ownedMesh || !go.GetComponent<MeshRenderer>().enabled)
                    throw new InvalidOperationException("Native area-light resources did not initialize");
                if (passed == 0 && !ownedMaterial.SetPass(0)) throw new InvalidOperationException("Target area-light shader pass unavailable");
                foreach (var entry in sample.Fields)
                    if (!Equals(fields[entry.Key].GetValue(light), entry.Value.ToObject(fields[entry.Key].FieldType)))
                        throw new InvalidOperationException("Native Init changed authored field: " + entry.Key);
                if (!(bool)init.Invoke(light, null)! || !ReferenceEquals(meshField.GetValue(light), ownedMesh) || !ReferenceEquals(proxyField.GetValue(light), ownedMaterial))
                    throw new InvalidOperationException("Repeat Init recreated owned resources");
                // The fixture stays inactive, so Unity never calls its Awake/
                // OnDestroy lifecycle. Release the explicitly initialized
                // native resources ourselves before destroying its GameObject.
                UnityEngine.Object.DestroyImmediate(ownedMaterial);
                UnityEngine.Object.DestroyImmediate(ownedMesh);
                UnityEngine.Object.DestroyImmediate(go);
                passed++;
                if (passed % 32 == 0) await UniTask.Yield();
            }
        }
        finally
        {
            foreach (var item in temporary) if (item) UnityEngine.Object.DestroyImmediate(item);
            scope?.Dispose();
            var unload = SceneManager.UnloadSceneAsync(scene);
            if (unload != null) await UniTask.WaitUntil(() => unload.isDone);
        }
        if (temporary.AsValueEnumerable().Any(item => item != null) || scene.isLoaded)
            throw new InvalidOperationException("Area-light fixture cleanup failed");
        var lifecycle = await RunLifecycle(input, token);
        return new { Passed = true, FixtureSha256 = ManifestRules.Hash(file), SourceLightsVerified = passed,
            NativeControlUntouched = nativeControl, NativeInitAndRepeatPassed = true, AuthoredFieldsPreserved = true,
            TargetShaders = shaderNames, ProxyShaderPassSupported = true, TemporaryObjectsDestroyed = true, TemporarySceneUnloaded = true, Lifecycle = lifecycle };
    }

    private static async UniTask<object> RunLifecycle(Input input, CancellationToken token)
    {
        var file = ManifestRules.Resolve(Plugin.Root, input.LifecycleBundle);
        if (ManifestRules.Hash(file) != input.LifecycleSha256) throw new InvalidDataException("Lifecycle bundle changed");
        AssetBundle? bundle = null;
        IDisposable? ownership = null;
        var owned = new List<UnityEngine.Object>();
        var callbacksBefore = Camera.onPreCull?.GetInvocationList().Length ?? 0;
        var lightCount = 0;
        var unityErrors = new List<string>();
        void CaptureError(string message, string stack, LogType type)
        {
            if (type == LogType.Exception || type == LogType.Error || type == LogType.Assert) unityErrors.Add(message + "\n" + stack);
        }
        Application.logMessageReceived += CaptureError;
        try
        {
            var request = AssetBundle.LoadFromFileAsync(file);
            await UniTask.WaitUntil(() => request.isDone);
            bundle = request.assetBundle;
            if (bundle == null) throw new InvalidDataException("Lifecycle bundle did not load");
            token.ThrowIfCancellationRequested();
            if (!bundle.GetAllScenePaths().AsValueEnumerable().Contains(input.LifecycleScene, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Lifecycle scene is absent from bundle");
            ownership = AreaLightBindings.ClaimProbePath(input.LifecycleScene);
            var load = SceneManager.LoadSceneAsync(input.LifecycleScene, LoadSceneMode.Additive);
            if (load == null) throw new InvalidOperationException("Lifecycle scene load did not start");
            await UniTask.WaitUntil(() => load.isDone);
            token.ThrowIfCancellationRequested();
            var scene = SceneManager.GetSceneByPath(input.LifecycleScene);
            var lights = scene.GetRootGameObjects().AsValueEnumerable().SelectMany(go => go.GetComponentsInChildren<AreaLight>(true)).ToArray();
            lightCount = lights.Length;
            if (lightCount != 3) throw new InvalidOperationException("Lifecycle fixture light census differs");
            foreach (var light in lights)
            {
                if (!light.isActiveAndEnabled || !(bool)AccessTools.Field(typeof(AreaLight), "bool_0").GetValue(light))
                    throw new InvalidOperationException("Area-light Awake/OnEnable initialization failed");
                var mesh = (Mesh)AccessTools.Field(typeof(AreaLight), "m_SourceMesh").GetValue(light);
                var material = (Material)AccessTools.Field(typeof(AreaLight), "m_ProxyMaterial").GetValue(light);
                if (mesh == null || material == null || material.shader.name != "Hidden/AreaLight")
                    throw new InvalidOperationException("Active area-light native resources missing");
                owned.Add(mesh); owned.Add(material); owned.Add(light.gameObject);
            }
            if ((Camera.onPreCull?.GetInvocationList().Length ?? 0) != callbacksBefore + lightCount)
                throw new InvalidOperationException("Area-light camera callbacks were not registered once per active light");
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
        }
        finally
        {
            var scene = SceneManager.GetSceneByPath(input.LifecycleScene);
            if (scene.IsValid() && scene.isLoaded)
            {
                var unload = SceneManager.UnloadSceneAsync(scene);
                if (unload != null) await UniTask.WaitUntil(() => unload.isDone);
            }
            ownership?.Dispose();
            if (bundle != null) bundle.Unload(true);
            Application.logMessageReceived -= CaptureError;
        }
        if (unityErrors.Count != 0) throw new InvalidOperationException("Unity errors during light lifecycle: " + string.Join("\n", unityErrors));
        if (owned.AsValueEnumerable().Any(item => item != null) || (Camera.onPreCull?.GetInvocationList().Length ?? 0) != callbacksBefore)
            throw new InvalidOperationException("Native area-light lifecycle leaked resources or camera callbacks");
        return new { Passed = true, ActiveLights = lightCount, BundleSha256 = input.LifecycleSha256,
            AwakeAndEnablePassed = true, NativeOwnedResourcesDestroyed = true, CameraCallbacksRestored = true, UnityErrors = 0 };
    }
}
