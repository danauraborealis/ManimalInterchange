using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using EFT;
using EFT.AssetsManager;
using Manimal.Interchange.Shared;
using Newtonsoft.Json;
using SPT.Common.Http;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Interchange.Client;

internal static class SceneLoader
{
    private static readonly List<AssetBundle> Bundles = new();
    private static readonly List<AssetBundleCreateRequest> PendingBundleLoads = new();
    private static readonly HashSet<string> ScenePaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<AsyncOperation> PendingSceneLoads = new();
    private static ScenesPreset? _ownedPreset;
    private static bool _busy;
    private static bool _cleaning;
    private static CancellationToken _loadingToken;
    internal static bool Owns(string path) => ScenePaths.Contains(path);
    internal static string LoadedManifestSha256 { get; private set; } = "";

    // EFT's callback consumes increments in scene units, not an absolute percentage.
    private const int PreparationSceneUnits = 3;
    private sealed class PreparationProgress(IProgress<float>? target) : IProgress<float>
    {
        private float _reported;
        private bool _closed;

        public void Report(float value)
        {
            if (_closed || float.IsNaN(value)) return;
            value = Math.Max(_reported, Math.Min(1f, value));
            var delta = value - _reported;
            _reported = value;
            if (delta > 0f) target?.Report(delta * PreparationSceneUnits);
        }

        internal void Close() => _closed = true;
    }

    internal static bool BeforeLoad(LoadScenesFromPresetOperation operation, ScenesPreset preset, ref Task result)
    {
        if (!preset || !string.Equals(preset.ServerName, "Interchange", StringComparison.OrdinalIgnoreCase)) return true;
        var path = Path.Combine(Plugin.Root, ManifestRules.FileName);
        if (!File.Exists(path)) return true;
        // Probe manifests never replace a normal raid's scenes or location data.
        result = LoadForRaid(operation, preset).AsTask();
        return false;
    }

    private static async UniTask LoadForRaid(LoadScenesFromPresetOperation operation, ScenesPreset native)
    {
        if (_busy || _cleaning || Bundles.Count != 0 || PendingBundleLoads.Count != 0)
        {
            operation.SetFailed("Manimal Interchange resources are still in use");
            return;
        }
        var acquired = false;
        var succeeded = false;
        var preparation = new PreparationProgress(operation._progress);
        try
        {
            var manifest = ReadManifest(out var manifestHash);
            if (manifest.Mode == "probe" || manifest.Mode == "development")
            {
                await LoadPresetReversePatch.LoadOriginal(operation, native);
                return;
            }
            if (manifest.Mode == "test" && !Plugin.AllowFullMapTest.Value)
                throw new InvalidDataException("Full Interchange test content is disabled in client configuration");
            if (manifest.Mode == "rework" && !manifest.Ready)
                throw new InvalidDataException("Full Interchange content has not passed its release checks");
            _busy = true;
            acquired = true;
            _loadingToken = operation._cancellationToken;
            _loadingToken.ThrowIfCancellationRequested();
            operation._totalScenesToLoad = native.GetTotalSceneCount() + PreparationSceneUnits;
            await Prepare(manifest, manifestHash, _loadingToken, preparation);
            preparation.Report(1f);
            preparation.Close();
            _ownedPreset = ClonePreset(native, manifest);
            operation._totalScenesToLoad = _ownedPreset.GetTotalSceneCount() + PreparationSceneUnits;
            Plugin.Log.LogInfo("Loading Interchange content " + manifest.ContentId + " with 18 replacement scenes");
            await LoadPresetReversePatch.LoadOriginal(operation, _ownedPreset);
            _loadingToken.ThrowIfCancellationRequested();
            if (operation.Failed || !string.IsNullOrEmpty(operation.GetLoadError()))
                throw new InvalidOperationException(operation.Error ?? operation.GetLoadError());
            succeeded = true;
        }
        catch (OperationCanceledException) { operation.SetCancelled(); }
        catch (Exception error) { Plugin.Log.LogError(error); operation.SetFailed("Manimal Interchange: " + error.Message); }
        finally
        {
            preparation.Close();
            if (acquired)
            {
                try { if (!succeeded) await Cleanup(); }
                catch (Exception error) { Plugin.Log.LogError("Interchange cleanup: " + error); }
                finally { _busy = false; _loadingToken = default; }
            }
        }
    }

    internal static ContentManifest ReadManifest(out string hash)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Plugin.Root, ManifestRules.FileName));
        using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        var text = Encoding.UTF8.GetString(bytes);
        var manifest = JsonConvert.DeserializeObject<ContentManifest>(text.TrimStart('\uFEFF'))!;
        ManifestRules.Validate(manifest);
        return manifest;
    }

    private static async UniTask Prepare(
        ContentManifest manifest,
        string manifestHash,
        CancellationToken token,
        IProgress<float>? preparationProgress = null)
    {
        var request = RequestHandler.GetJsonAsync(ManifestRules.CapabilityRoute);
        var timeout = Task.Delay(TimeSpan.FromSeconds(15), token);
        if (await Task.WhenAny(request, timeout) != request)
        {
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("Server content handshake timed out");
        }
        var capability = JsonConvert.DeserializeObject<ServerCapability>(await request)!;
        var root = Plugin.Root;
        var nativeData = Application.dataPath;
        IProgress<float>? verificationProgress = preparationProgress == null
            ? null
            : new System.Progress<float>(value => preparationProgress.Report(0.02f + value * 0.68f));
        preparationProgress?.Report(0.02f);
        await UniTask.RunOnThreadPool(() =>
        {
            ManifestRules.CheckCapability(manifest, manifestHash, capability, manifest.Mode == "probe");

            var nativeCount = manifest.NativeFiles.Count;
            var nativeIndex = 0;
            foreach (var file in manifest.NativeFiles)
            {
                token.ThrowIfCancellationRequested();
                var fileIndex = nativeIndex++;
                Action<float>? fileProgress = verificationProgress == null
                    ? null
                    : value => verificationProgress.Report(nativeCount == 0
                        ? 0f
                        : 0.75f * (fileIndex + value) / nativeCount);
                InterchangeVerifiedFiles.Verify(nativeData, file, token, fileProgress);
            }

            var payloadCount = manifest.Bundles.Count + manifest.Sidecars.Count;
            var payloadIndex = 0;
            foreach (var file in manifest.Bundles)
            {
                token.ThrowIfCancellationRequested();
                var fileIndex = payloadIndex++;
                Action<float>? fileProgress = verificationProgress == null
                    ? null
                    : value => verificationProgress.Report(0.75f + 0.25f * (fileIndex + value) / payloadCount);
                InterchangeVerifiedFiles.Verify(root, file, token, fileProgress);
            }

            foreach (var file in manifest.Sidecars)
            {
                token.ThrowIfCancellationRequested();
                var fileIndex = payloadIndex++;
                Action<float>? fileProgress = verificationProgress == null
                    ? null
                    : value => verificationProgress.Report(0.75f + 0.25f * (fileIndex + value) / payloadCount);
                InterchangeVerifiedFiles.Verify(root, file, token, fileProgress);
            }
        }, cancellationToken: token);
        token.ThrowIfCancellationRequested();
        preparationProgress?.Report(0.70f);
        await UniTask.SwitchToMainThread(token);
        var bundleIndex = 0;
        foreach (var entry in manifest.Bundles)
        {
            token.ThrowIfCancellationRequested();
            // Unity cannot cancel an in-flight bundle request; retain ownership before observing cancellation.
            var requestBundle = AssetBundle.LoadFromFileAsync(ManifestRules.Resolve(root, entry.Path));
            if (requestBundle == null)
            {
                throw new InvalidDataException("Unity could not start loading " + entry.Path);
            }
            PendingBundleLoads.Add(requestBundle);

            while (!requestBundle.isDone)
            {
                preparationProgress?.Report(0.70f + 0.30f * (bundleIndex + requestBundle.progress) / manifest.Bundles.Count);
                await UniTask.Yield();
            }

            var bundle = requestBundle.assetBundle;
            PendingBundleLoads.Remove(requestBundle);
            if (!bundle) throw new InvalidDataException("Unity could not load " + entry.Path);
            Bundles.Add(bundle);
            token.ThrowIfCancellationRequested();
            bundleIndex++;
            preparationProgress?.Report(0.70f + 0.30f * bundleIndex / manifest.Bundles.Count);
        }
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in Bundles)
            foreach (var path in bundle.GetAllScenePaths())
                if (!available.Add(path)) throw new InvalidDataException("Duplicate bundled scene paths");
        foreach (var scene in manifest.Scenes)
            if (!available.Contains(scene.ReplacementPath)) throw new InvalidDataException("A declared replacement scene is missing from the bundles");
        foreach (var scene in manifest.Scenes) ScenePaths.Add(scene.ReplacementPath);
        InterchangeSidecars.Activate(manifest);
        LoadedManifestSha256 = manifestHash;
    }

    internal static ScenesPreset ClonePreset(ScenesPreset native, ContentManifest manifest)
    {
        if (native.ChildPresets.Length != 0 || !string.IsNullOrEmpty(native.ActiveSceneName) || native._scenesResourceKeys.Count != manifest.Scenes.Count)
            throw new InvalidDataException("Native preset structure differs from audit");
        for (var i = 0; i < manifest.Scenes.Count; i++)
            if (native._scenesResourceKeys[i].path != manifest.Scenes[i].OriginalPath || native._scenesResourceKeys[i].onlyOffline != manifest.Scenes[i].OnlyOffline)
                throw new InvalidDataException("Native preset order/flags differ from audit");
        var clone = UnityEngine.Object.Instantiate(native);
        clone._scenesResourceKeys = new List<SceneResourceKey>(manifest.Scenes.Count);
        foreach (var scene in manifest.Scenes)
            clone._scenesResourceKeys.Add(new SceneResourceKey
                { path = scene.ReplacementPath, rcid = scene.ReplacementPath, onlyOffline = scene.OnlyOffline });
        return clone;
    }

    internal static async UniTask<object> RunProbe(CancellationToken token)
    {
        if (_busy || _cleaning || Bundles.Count != 0 || PendingBundleLoads.Count != 0) throw new InvalidOperationException("Interchange resources are still in use");
        _busy = true;
        var results = new List<object>();
        try
        {
            var manifest = ReadManifest(out var manifestHash);
            if (manifest.Mode != "probe") throw new InvalidDataException("An explicit probe manifest is required");
            _loadingToken = token;
            await Prepare(manifest, manifestHash, token);
            // Exercise the target preset loader with a faithful native-preset fixture.
            var native = ScriptableObject.CreateInstance<ScenesPreset>();
            try
            {
                native.ServerName = "Interchange";
                native.ChildPresets = Array.Empty<ScenesPreset>();
                native._activeSceneName = "";
                native._scenesResourceKeys = new List<SceneResourceKey>(manifest.Scenes.Count);
                foreach (var scene in manifest.Scenes)
                    native._scenesResourceKeys.Add(new SceneResourceKey
                        { path = scene.OriginalPath, rcid = scene.OriginalPath, onlyOffline = scene.OnlyOffline });
                _ownedPreset = ClonePreset(native, manifest);
            }
            finally { UnityEngine.Object.Destroy(native); }
            _ownedPreset.DisableServerScenes(false);
            var operation = new LoadScenesFromPresetOperation
            {
                _loadFirstAsSingle = false, _loadParallel = true, _allowSceneActivation = true,
                _cancellationToken = token, _totalScenesToLoad = _ownedPreset.GetTotalSceneCount()
            };
            await LoadPresetReversePatch.LoadOriginal(operation, _ownedPreset);
            token.ThrowIfCancellationRequested();
            if (operation.Failed || !string.IsNullOrEmpty(operation.GetLoadError())) throw new InvalidOperationException(operation.Error ?? operation.GetLoadError());
            foreach (var mapping in manifest.Scenes)
            {
                var scene = SceneManager.GetSceneByPath(mapping.ReplacementPath);
                if (!scene.IsValid() || !scene.isLoaded) throw new InvalidOperationException("Scene did not load: " + mapping.ReplacementPath);
                var componentCount = 0;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var component in root.GetComponentsInChildren<Component>(true))
                    {
                        if (!component) throw new InvalidOperationException("Missing script in " + scene.path);
                        componentCount++;
                    }
                results.Add(new { scene = scene.path, roots = scene.rootCount, components = componentCount });
            }
            var scripts = SceneManager.GetSceneByPath(manifest.Scenes[0].ReplacementPath);
            var doorCount = 0;
            foreach (var root in scripts.GetRootGameObjects()) doorCount += root.GetComponentsInChildren<EFT.Interactive.Door>(true).Length;
            if (doorCount != 1) throw new InvalidOperationException("Representative target Door did not resolve");
            return new { passed = true, manifest.ContentId, manifest.TargetClientBuild, scenes = results, targetDoorCount = doorCount, presetLoader = true };
        }
        finally
        {
            try { await Cleanup(); }
            finally { _busy = false; _loadingToken = default; }
        }
    }

    internal static bool BeforeSceneLoad(ResourceKey key, LoadSceneMode mode, bool activate, Action<float> progress, ref LoadSceneOperation result)
    {
        if (key == null || !ScenePaths.Contains(key.path)) return true;
        var owned = new OwnedSceneOperation();
        result = owned;
        owned.Begin(key.path, mode, activate, progress, _loadingToken).Forget();
        return false;
    }

    internal static async UniTask<object> RunPausedCancellationProbe(CancellationToken token)
    {
        if (_busy || _cleaning || Bundles.Count != 0 || PendingBundleLoads.Count != 0) throw new InvalidOperationException("Interchange resources are still in use");
        var manifest = ReadManifest(out var manifestHash);
        if (manifest.Mode != "probe") throw new InvalidDataException("Paused cancellation requires the skeleton probe manifest");
        _busy = true;
        var cases = new List<object>();
        try
        {
            foreach (var atNinetyPercent in new[] { false, true })
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                _loadingToken = cancellation.Token;
                AsyncOperation? request = null;
                var path = manifest.Scenes[0].ReplacementPath;
                var beforeScenes = SceneManager.sceneCount;
                try
                {
                    await Prepare(manifest, manifestHash, cancellation.Token);
                    var operation = new OwnedSceneOperation();
                    operation.Begin(path, LoadSceneMode.Additive, false, _ => { }, cancellation.Token).Forget();
                    await UniTask.WaitUntil(() => operation.AsyncOperation != null || !string.IsNullOrEmpty(operation.Error), cancellationToken: token);
                    request = operation.AsyncOperation;
                    if (request == null) throw new InvalidOperationException(operation.Error);
                    if (atNinetyPercent)
                    {
                        await UniTask.WaitUntil(() => operation.Succeed || !string.IsNullOrEmpty(operation.Error), cancellationToken: token);
                        if (!operation.Succeed || request.isDone || request.progress < .9f || request.allowSceneActivation)
                            throw new InvalidOperationException("The owned request did not pause before activation");
                    }
                    cancellation.Cancel();
                }
                finally { await Cleanup(); }
                if (request == null || !request.isDone || PendingSceneLoads.Count != 0 || Bundles.Count != 0)
                    throw new InvalidOperationException("Cancelled scene request or bundle remained owned");
                var remaining = SceneManager.GetSceneByPath(path);
                if ((remaining.IsValid() && remaining.isLoaded) || SceneManager.sceneCount != beforeScenes)
                    throw new InvalidOperationException("Cancelled scene was not fully unloaded");
                token.ThrowIfCancellationRequested();
                cases.Add(new { CancelledAtNinetyPercent = atNinetyPercent, RequestFinishedBeforeBundleRelease = true,
                    SceneUnloaded = true, OwnershipReleased = true });
            }
            return new { Passed = true, Cases = cases };
        }
        finally
        {
            try { await Cleanup(); }
            finally { _busy = false; _loadingToken = default; }
        }
    }

    private sealed class OwnedSceneOperation : LoadSceneOperation
    {
        internal async UniTaskVoid Begin(string path, LoadSceneMode mode, bool activate, Action<float> progress, CancellationToken token)
        {
            Info = "Manimal Interchange: " + path;
            try
            {
                token.ThrowIfCancellationRequested();
                AsyncOperation = SceneManager.LoadSceneAsync(path, mode);
                if (AsyncOperation == null) throw new IOException("Unity rejected scene " + path);
                PendingSceneLoads.Add(AsyncOperation);
                // Let the target operation control activation after all scenes reach 90%.
                AsyncOperation.allowSceneActivation = activate;
                while (!AsyncOperation.isDone && (activate || AsyncOperation.progress < 0.9f))
                {
                    if (token.IsCancellationRequested) AsyncOperation.allowSceneActivation = true;
                    progress?.Invoke(AsyncOperation.progress);
                    await UniTask.Yield();
                }
                token.ThrowIfCancellationRequested();
                Succeed = true;
            }
            catch (OperationCanceledException) { Error = "Scene load cancelled"; }
            catch (Exception error) { Error = error.Message; Plugin.Log.LogError(Info + ": " + error); }
        }
    }

    private static async UniTask Cleanup()
    {
        await UniTask.SwitchToMainThread();
        if (_cleaning) { await UniTask.WaitUntil(() => !_cleaning); return; }
        _cleaning = true;
        try
        {
        // A progress callback can fail while Unity is still completing a
        // bundle request. Finish and retain every tracked result before
        // unloading anything else so partial loads cannot leak.
        for (var i = 0; i < PendingBundleLoads.Count; i++)
        {
            var request = PendingBundleLoads[i];
            if (!request.isDone) await UniTask.WaitUntil(() => request.isDone);
            var bundle = request.assetBundle;
            if (bundle && !Bundles.Contains(bundle)) Bundles.Add(bundle);
        }
        PendingBundleLoads.Clear();
        // Requests paused at 90% remain owned even after their load operation
        // returned to the native activation coordinator. Finish them before
        // inspecting loaded scenes or releasing any referenced bundle.
        for (var i = 0; i < PendingSceneLoads.Count; i++)
        {
            var request = PendingSceneLoads[i];
            if (!request.isDone) request.allowSceneActivation = true;
        }
        for (var i = 0; i < PendingSceneLoads.Count; i++)
        {
            var request = PendingSceneLoads[i];
            if (!request.isDone) await UniTask.WaitUntil(() => request.isDone);
        }
        PendingSceneLoads.Clear();
        foreach (var path in ScenePaths)
        {
            var scene = SceneManager.GetSceneByPath(path);
            if (!scene.IsValid() || !scene.isLoaded) continue;
            var unloading = SceneManager.UnloadSceneAsync(scene);
            if (unloading != null) await UniTask.WaitUntil(() => unloading.isDone);
        }
        foreach (var bundle in Bundles) if (bundle) bundle.Unload(true);
        Bundles.Clear();
        ScenePaths.Clear();
        InterchangeSidecars.Clear();
        LoadedManifestSha256 = "";
        if (_ownedPreset) UnityEngine.Object.Destroy(_ownedPreset);
        _ownedPreset = null;
        }
        finally { _cleaning = false; }
    }

    internal static void SceneUnloaded(Scene scene)
    {
        if (_busy || _cleaning || !ScenePaths.Contains(scene.path)) return;
        foreach (var path in ScenePaths)
        {
            var remaining = SceneManager.GetSceneByPath(path);
            if (remaining.IsValid() && remaining.isLoaded) return;
        }
        Cleanup().Forget();
    }
}
