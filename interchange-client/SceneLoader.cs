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
    private static readonly object ProducerGate = new();
    private static readonly List<AssetBundle> Bundles = new();
    private static readonly List<AssetBundleCreateRequest> PendingBundleLoads = new();
    private static readonly HashSet<string> ScenePaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<AsyncOperation> PendingSceneLoads = new();
    private static ScenesPreset? _ownedPreset;
    private static bool _busy;
    private static bool _cleaning;
    private static bool _cleanupPending;
    private static CancellationToken _loadingToken;
    private static CancellationTokenSource _shutdownCancellation = new();
    private static TaskCompletionSource<bool>? _producersIdle;
    private static List<Action>? _teardownCallbacks;
    private static int _activeProducerCount;
    private static bool _shutdownRequested;
    private static bool _teardownStarted;
    internal static bool Owns(string path) => ScenePaths.Contains(path);
    internal static string LoadedManifestSha256 { get; private set; } = "";

    private sealed class ProducerLease : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            EndProducer();
        }
    }

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

    internal static void Initialize()
    {
        lock (ProducerGate)
        {
            if (!_shutdownRequested || _activeProducerCount != 0 || _cleaning || HasOutstandingOwnership || Bundles.Count != 0 || _ownedPreset is not null || _teardownCallbacks is not null) return;
            _shutdownCancellation = new CancellationTokenSource();
            _shutdownRequested = false;
            _teardownStarted = false;
        }
    }

    private static bool IsShutdownRequested
    {
        get { lock (ProducerGate) return _shutdownRequested; }
    }

    private static ProducerLease? TryBeginProducer()
    {
        lock (ProducerGate)
        {
            if (_shutdownRequested) return null;
            if (_activeProducerCount == 0)
                _producersIdle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeProducerCount++;
            return new ProducerLease();
        }
    }

    private static void EndProducer()
    {
        TaskCompletionSource<bool>? idle = null;
        lock (ProducerGate)
        {
            if (_activeProducerCount == 0) return;
            _activeProducerCount--;
            if (_activeProducerCount == 0)
            {
                idle = _producersIdle;
                _producersIdle = null;
            }
        }
        idle?.TrySetResult(true);
    }

    private static async UniTask WaitForProducers()
    {
        while (true)
        {
            Task idle;
            lock (ProducerGate)
            {
                if (_activeProducerCount == 0) return;
                idle = _producersIdle?.Task ?? Task.CompletedTask;
            }
            await idle;
        }
    }

    private static CancellationTokenSource LinkShutdown(CancellationToken token)
    {
        lock (ProducerGate)
            return CancellationTokenSource.CreateLinkedTokenSource(token, _shutdownCancellation.Token);
    }

    private static void ThrowIfShutdownRequested(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (IsShutdownRequested) throw new OperationCanceledException(token);
    }

    private static void RequestShutdown()
    {
        CancellationTokenSource cancellation;
        lock (ProducerGate)
        {
            _shutdownRequested = true;
            cancellation = _shutdownCancellation;
        }
        try { cancellation.Cancel(); }
        catch (Exception error) { if (Plugin.Log != null) Plugin.Log.LogError("Interchange shutdown cancellation failed: " + error); }
    }

    private static bool HasOutstandingOwnership => PendingBundleLoads.Count != 0 || PendingSceneLoads.Count != 0 || ScenePaths.Count != 0;

    private static bool IsReleased => !HasOutstandingOwnership && Bundles.Count == 0 && _ownedPreset is null;

    private static void AddTeardownCallback(Action callback)
    {
        lock (ProducerGate)
        {
            (_teardownCallbacks ??= new List<Action>()).Add(callback);
        }
    }

    private static void NotifyTeardownCallbacksIfReleased()
    {
        List<Action>? callbacks = null;
        lock (ProducerGate)
        {
            if (!IsReleased || _teardownCallbacks is null || _teardownCallbacks.Count == 0) return;
            callbacks = _teardownCallbacks;
            _teardownCallbacks = null;
        }
        foreach (var callback in callbacks)
        {
            try { callback(); }
            catch (Exception error) { if (Plugin.Log != null) Plugin.Log.LogError("Interchange teardown callback failed: " + error); }
        }
    }

    internal static bool BeforeLoad(LoadScenesFromPresetOperation operation, ScenesPreset preset, ref Task result)
    {
        if (!preset || !string.Equals(preset.ServerName, "Interchange", StringComparison.OrdinalIgnoreCase)) return true;
        var path = Path.Combine(Plugin.Root, ManifestRules.FileName);
        if (!File.Exists(path)) return true;
        if (IsShutdownRequested)
        {
            if (operation._cancellationToken.IsCancellationRequested) operation.SetCancelled();
            else operation.SetFailed("Manimal Interchange is shutting down");
            result = Task.CompletedTask;
            return false;
        }
        // Probe manifests never replace a normal raid's scenes or location data.
        result = LoadForRaid(operation, preset).AsTask();
        return false;
    }

    private static async UniTask LoadForRaid(LoadScenesFromPresetOperation operation, ScenesPreset native)
    {
        var producer = TryBeginProducer();
        if (producer is null)
        {
            if (operation._cancellationToken.IsCancellationRequested) operation.SetCancelled();
            else operation.SetFailed("Manimal Interchange is shutting down");
            return;
        }
        using (producer)
        {
        using var operationCancellation = LinkShutdown(operation._cancellationToken);
        operation._cancellationToken = operationCancellation.Token;
        var acquired = false;
        var succeeded = false;
        var preparation = new PreparationProgress(operation._progress);
        try
        {
            await RetryPendingCleanup();
            ThrowIfShutdownRequested(operation._cancellationToken);
            if (_busy || _cleaning || Bundles.Count != 0 || PendingBundleLoads.Count != 0)
            {
                operation.SetFailed("Manimal Interchange resources are still in use");
                return;
            }
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
            ThrowIfShutdownRequested(_loadingToken);
            operation._totalScenesToLoad = native.GetTotalSceneCount() + PreparationSceneUnits;
            await Prepare(manifest, manifestHash, _loadingToken, preparation);
            preparation.Report(1f);
            preparation.Close();
            ThrowIfShutdownRequested(_loadingToken);
            _ownedPreset = ClonePreset(native, manifest);
            operation._totalScenesToLoad = _ownedPreset.GetTotalSceneCount() + PreparationSceneUnits;
            Plugin.Log.LogInfo("Loading Interchange content " + manifest.ContentId + " with 18 replacement scenes");
            await LoadPresetReversePatch.LoadOriginal(operation, _ownedPreset);
            ThrowIfShutdownRequested(_loadingToken);
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
        ThrowIfShutdownRequested(token);
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
        ThrowIfShutdownRequested(token);
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
        ThrowIfShutdownRequested(token);
        foreach (var scene in manifest.Scenes) ScenePaths.Add(scene.ReplacementPath);
        InterchangeSidecars.Activate(manifest);
        // All owned-scene ModulePatch wrappers must be active before the
        // first replacement scene is handed to Unity; this also covers the
        // menu-only loader probes that call Prepare directly.
        ThrowIfShutdownRequested(token);
        InterchangePatchLifecycle.Enable();
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
        var producer = TryBeginProducer();
        if (producer is null) throw new OperationCanceledException("Manimal Interchange is shutting down");
        using (producer)
        using (var operationCancellation = LinkShutdown(token))
        {
        token = operationCancellation.Token;
        await RetryPendingCleanup();
        ThrowIfShutdownRequested(token);
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
            ThrowIfShutdownRequested(token);
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
    }

    internal static bool BeforeSceneLoad(ResourceKey key, LoadSceneMode mode, bool activate, Action<float> progress, ref LoadSceneOperation result)
    {
        if (key == null || !ScenePaths.Contains(key.path)) return true;
        if (IsShutdownRequested)
        {
            var rejected = new OwnedSceneOperation();
            rejected.Reject("Scene load rejected while Interchange is shutting down");
            result = rejected;
            return false;
        }
        var owned = new OwnedSceneOperation();
        result = owned;
        owned.Begin(key.path, mode, activate, progress, _loadingToken).Forget();
        return false;
    }

    internal static async UniTask<object> RunPausedCancellationProbe(CancellationToken token)
    {
        var producer = TryBeginProducer();
        if (producer is null) throw new OperationCanceledException("Manimal Interchange is shutting down");
        using (producer)
        using (var operationCancellation = LinkShutdown(token))
        {
        token = operationCancellation.Token;
        await RetryPendingCleanup();
        ThrowIfShutdownRequested(token);
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
    }

    private sealed class OwnedSceneOperation : LoadSceneOperation
    {
        internal void Reject(string error) => Error = error;

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

    private static async UniTask RetryPendingCleanup()
    {
        if (!_cleanupPending) return;
        if (_cleaning) await UniTask.WaitUntil(() => !_cleaning);
        if (_cleanupPending) await Cleanup();
    }

    private static async UniTask DrainPendingSceneLoads()
    {
        // Requests paused at 90% remain owned even after their load operation
        // returned to the native activation coordinator. Unity queues
        // activation, so every request must be released first; waiting for a
        // later request before releasing an earlier one can deadlock the
        // queue. If one setter fails, retain that request and all unresolved
        // successors for a later retry.
        var blockedSceneRequests = new HashSet<AsyncOperation>();
        var activationFailed = false;
        for (var i = 0; i < PendingSceneLoads.Count; i++)
        {
            var request = PendingSceneLoads[i];
            if (request == null)
            {
                PendingSceneLoads.RemoveAt(i--);
                continue;
            }
            try
            {
                if (!request.isDone) request.allowSceneActivation = true;
            }
            catch (Exception error)
            {
                activationFailed = true;
                if (!request.isDone) blockedSceneRequests.Add(request);
                if (Plugin.Log != null) Plugin.Log.LogError("Interchange scene activation cleanup failed: " + error);
            }
            if (activationFailed && !request.isDone) blockedSceneRequests.Add(request);
        }
        for (var i = PendingSceneLoads.Count - 1; i >= 0; i--)
        {
            var request = PendingSceneLoads[i];
            try
            {
                if (request == null)
                {
                    PendingSceneLoads.RemoveAt(i);
                    continue;
                }
                if (blockedSceneRequests.Contains(request) && !request.isDone) continue;
                if (!request.isDone) await UniTask.WaitUntil(() => request.isDone);
                if (request.isDone) PendingSceneLoads.RemoveAt(i);
            }
            catch (Exception error)
            {
                if (Plugin.Log != null) Plugin.Log.LogError("Interchange scene request cleanup failed: " + error);
            }
        }
    }

    private static async UniTask Cleanup()
    {
        await UniTask.SwitchToMainThread();
        await CleanupCore();
    }

    private static async UniTask CleanupCore()
    {
        if (_cleaning)
        {
            await UniTask.WaitUntil(() => !_cleaning);
            if (!_cleanupPending) return;
        }
        _cleaning = true;
        _cleanupPending = true;
        var released = false;
        try
        {
            // A progress callback can fail while Unity is still completing a
            // bundle request. Finish each tracked result before unloading
            // anything else; a request that cannot be observed remains owned
            // for a later cleanup retry.
            for (var i = PendingBundleLoads.Count - 1; i >= 0; i--)
            {
                var request = PendingBundleLoads[i];
                try
                {
                    if (request == null)
                    {
                        PendingBundleLoads.RemoveAt(i);
                        continue;
                    }
                    if (!request.isDone) await UniTask.WaitUntil(() => request.isDone);
                    var bundle = request.assetBundle;
                    if (bundle && !Bundles.Contains(bundle)) Bundles.Add(bundle);
                    PendingBundleLoads.RemoveAt(i);
                }
                catch (Exception error)
                {
                    if (Plugin.Log != null) Plugin.Log.LogError("Interchange bundle request cleanup failed: " + error);
                }
            }

            await DrainPendingSceneLoads();
            if (PendingSceneLoads.Count != 0)
            {
                // An activation failure can leave a Unity queue blocked. Do
                // not sweep scene identities or queue unloads behind it; keep
                // every owned resource for the scene-unloaded retry path.
                if (Plugin.Log != null) Plugin.Log.LogWarning("Interchange scene requests remain paused; retaining ownership for retry");
                return;
            }

            // Unload one owned scene at a time. Keep any scene that Unity
            // rejects or leaves loaded so the scene-unloaded subscription can
            // retry without losing ownership of a live scene.
            var ownedPaths = new List<string>(ScenePaths);
            foreach (var path in ownedPaths)
            {
                try
                {
                    var scene = SceneManager.GetSceneByPath(path);
                    if (!scene.IsValid() || !scene.isLoaded)
                    {
                        ScenePaths.Remove(path);
                        continue;
                    }
                    var unloading = SceneManager.UnloadSceneAsync(scene);
                    if (unloading == null)
                    {
                        if (Plugin.Log != null) Plugin.Log.LogWarning("Interchange scene unload was rejected: " + path);
                        continue;
                    }
                    if (!unloading.isDone) await UniTask.WaitUntil(() => unloading.isDone);
                    var remaining = SceneManager.GetSceneByPath(path);
                    if (!remaining.IsValid() || !remaining.isLoaded) ScenePaths.Remove(path);
                    else if (Plugin.Log != null) Plugin.Log.LogWarning("Interchange scene remains loaded after unload: " + path);
                }
                catch (Exception error)
                {
                    if (Plugin.Log != null) Plugin.Log.LogError("Interchange scene cleanup failed for " + path + ": " + error);
                }
            }

            // Bundles must remain alive while any owned scene or request is
            // still present. Once those are gone, unload each bundle
            // independently and retain only failures for a later retry.
            if (PendingBundleLoads.Count == 0 && PendingSceneLoads.Count == 0 && ScenePaths.Count == 0)
            {
                for (var i = Bundles.Count - 1; i >= 0; i--)
                {
                    var bundle = Bundles[i];
                    try
                    {
                        if (bundle) bundle.Unload(true);
                        Bundles.RemoveAt(i);
                    }
                    catch (Exception error)
                    {
                        if (Plugin.Log != null) Plugin.Log.LogError("Interchange bundle cleanup failed: " + error);
                    }
                }

                // Patches remain installed until owned scenes and pending
                // requests have finished tearing down. Their callbacks may
                // still observe Unity objects during destruction.
                try { InterchangePatchLifecycle.Disable(); }
                catch (Exception error) { if (Plugin.Log != null) Plugin.Log.LogError("Interchange patch teardown failed: " + error); }
                InterchangeSidecars.Clear();
                LoadedManifestSha256 = "";
                if (_ownedPreset) UnityEngine.Object.Destroy(_ownedPreset);
                _ownedPreset = null;
            }

            released = IsReleased;
            _cleanupPending = !released;
            if (!released && Plugin.Log != null)
                Plugin.Log.LogWarning("Interchange cleanup retained ownership for a later retry");
        }
        finally
        {
            // Do not clear ownership in the failure path: surviving scenes,
            // requests, and bundles are the handles required for retrying.
            _cleaning = false;
        }
        if (released) NotifyTeardownCallbacksIfReleased();
    }

    internal static void Teardown(Action? onReleased = null)
    {
        if (onReleased is not null) AddTeardownCallback(onReleased);
        RequestShutdown();
        var alreadyStarted = false;
        lock (ProducerGate)
        {
            if (_teardownStarted)
                alreadyStarted = true;
            else
                _teardownStarted = true;
        }
        if (alreadyStarted)
        {
            NotifyTeardownCallbacksIfReleased();
            return;
        }
        TeardownAsync().Forget();
    }

    private static async UniTask TeardownAsync()
    {
        try
        {
            // A raid loader may still be in handshake, file verification, or
            // Unity request acquisition. Cancellation alone is not enough:
            // wait until every producer has left before releasing resources.
            await WaitForProducers();
            await Cleanup();
        }
        catch (Exception error)
        {
            if (Plugin.Log != null) Plugin.Log.LogError("Interchange teardown failed: " + error);
        }
        finally { NotifyTeardownCallbacksIfReleased(); }
    }

    internal static void SceneUnloaded(Scene scene)
    {
        if (_busy || _cleaning || (!_cleanupPending && !ScenePaths.Contains(scene.path))) return;
        if (!_cleanupPending)
        {
            foreach (var path in ScenePaths)
            {
                var remaining = SceneManager.GetSceneByPath(path);
                if (remaining.IsValid() && remaining.isLoaded) return;
            }
        }
        if (_cleanupPending || ScenePaths.Count != 0) Cleanup().Forget();
    }
}
