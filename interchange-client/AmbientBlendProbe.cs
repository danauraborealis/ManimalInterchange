using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Audio.AmbientSubsystem;
using Audio.AmbientSubsystem.Data;
using Audio.AuxiliaryAudioUtils;
using Audio.Data;
using Audio.SpatialSystem;
using Audio.SpatialSystem.Data;
using Audio.SpatialSystem.Utils;
using Comfort.Common;
using CommonAssets.Scripts.Audio;
using Cysharp.Threading.Tasks;
using EFT;
using HarmonyLib;
using Manimal.Interchange.Components;
using Manimal.Interchange.Shared;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;

namespace Manimal.Interchange.Client;

/// <summary>
/// Runtime verification for the two converted ambient blenders and their
/// environment system.  It compares the adapter's portal result with an
/// independent implementation of the source score and, when the real game
/// mixer is available, reads back every native mixer value and fader step.
/// </summary>
internal static class AmbientBlendProbe
{
    private sealed class Input
    {
        public string Bundle = "";
        public string Sha256 = "";
        public string Scene = "";
        public int OwnedBlenders = 2;
        public int OwnedSystems = 1;
    }

    internal static async UniTask<object> Run(CancellationToken token)
    {
        var manifestPath = Path.Combine(Plugin.Root, "ambient-blend-validation.json");
        var input = JsonConvert.DeserializeObject<Input>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("Ambient blend fixture manifest missing");
        var bundlePath = ManifestRules.Resolve(Plugin.Root, input.Bundle);
        if (ManifestRules.Hash(bundlePath) != input.Sha256) throw new InvalidDataException("Ambient blend fixture changed");

        var beforeSceneCount = SceneManager.sceneCount;
        var beforeBlenderCount = AmbientBlendBindings.BlenderCount;
        var beforeSystemCount = AmbientBlendBindings.SystemCount;
        var checks = new List<string>();
        var deferred = new List<string>();
        var unityErrors = new List<string>();
        var mixerRestore = new Dictionary<string, float>(StringComparer.Ordinal);
        AudioMixer? master = null;
        AmbientBlendBindings.BlenderBinding? blenderA = null;
        AmbientBlendBindings.BlenderBinding? blenderB = null;
        AmbientBlendBindings.SystemBinding? ownedSystem = null;
        AmbientBlendBindings.SystemBinding? scoreBinding = null;
        AssetBundle? bundle = null;
        Scene scene = default;
        Scene graphScene = default;
        bool mixerChecksRan = false;
        bool neighborChecksRan = false;

        void Check(bool pass, string name)
        {
            token.ThrowIfCancellationRequested();
            if (!pass) throw new InvalidDataException(name);
            checks.Add(name);
        }

        void CaptureUnityError(string message, string stack, LogType kind)
        {
            if (kind == LogType.Error || kind == LogType.Exception || kind == LogType.Assert)
                unityErrors.Add(message + "\n" + stack);
        }

        Application.logMessageReceived += CaptureUnityError;
        try
        {
            var request = AssetBundle.LoadFromFileAsync(bundlePath);
            await UniTask.WaitUntil(() => request.isDone);
            bundle = request.assetBundle;
            Check(bundle != null, "Ambient blend fixture bundle loaded");
            Check(bundle!.GetAllScenePaths().Length == 1 &&
                string.Equals(bundle.GetAllScenePaths()[0], input.Scene, StringComparison.Ordinal),
                "Exact ambient fixture scene");

            var load = SceneManager.LoadSceneAsync(input.Scene, LoadSceneMode.Additive);
            if (load == null) throw new InvalidOperationException("Ambient blend fixture scene load did not start");
            await UniTask.WaitUntil(() => load.isDone);
            scene = SceneManager.GetSceneByPath(input.Scene);
            Check(scene.IsValid() && scene.isLoaded, "Ambient fixture scene loaded");
            var roots = scene.GetRootGameObjects();
            Check(roots.Length == 1, "Ambient fixture has one root");
            var root = roots[0];
            Check(!root.activeSelf, "Ambient fixture remains inactive before validation");

            var policies = root.GetComponentsInChildren<AmbientBlendPolicy>(true);
            var targetBlenders = root.GetComponentsInChildren<AmbientSoundBlender>(true);
            var targetSystems = root.GetComponentsInChildren<EnvironmentSoundBlendSystem>(true);
            Check(policies.Length == 3, "Fixture has exactly two blender policies and one system policy");
            Check(targetBlenders.Length == 3 && targetSystems.Length == 2,
                "Fixture has two owned and one unowned blender/system control pair");

            var policyA = FindPolicy(policies, "probe/ambient/blender-a");
            var policyB = FindPolicy(policies, "probe/ambient/blender-b");
            var systemPolicy = FindPolicy(policies, "probe/ambient/system");
            var targetA = policyA.GetComponent<AmbientSoundBlender>();
            var targetB = policyB.GetComponent<AmbientSoundBlender>();
            var targetSystem = systemPolicy.GetComponent<EnvironmentSoundBlendSystem>();
            if (targetA == null || targetB == null || targetSystem == null)
                throw new InvalidDataException("Ambient fixture owner references are missing");
            Check(systemPolicy.AmbientSoundBlenders.Length == 2 &&
                ReferenceEquals(systemPolicy.AmbientSoundBlenders[0], targetA) &&
                ReferenceEquals(systemPolicy.AmbientSoundBlenders[1], targetB),
                "System policy preserves both target blender references");
            Check(policyA.FadeInType == 0 && policyA.FadeOutType == 0 &&
                Mathf.Abs(policyA.FadeInDuration - 3f) < .0001f && Mathf.Abs(policyA.FadeOutDuration - .8f) < .0001f,
                "First source directional fade values survive fixture serialization");
            Check(policyB.FadeInType == 6 && policyB.FadeOutType == 5 &&
                Mathf.Abs(policyB.FromOutToInTransitionTime - .1f) < .0001f &&
                Mathf.Abs(policyB.FromInToOutTransitionTime - 2f) < .0001f,
                "Second source logarithmic/inverse-exponential fades survive fixture serialization");
            Check(Mathf.Abs(systemPolicy.DepthCalculationMult - 2.7f) < .0001f &&
                Mathf.Abs(systemPolicy.ConnectedRoomPenalty - .3f) < .0001f &&
                Mathf.Abs(systemPolicy.NeighborRoomPenalty - .4f) < .0001f &&
                !systemPolicy.IgnoreDistanceRatio,
                "Source portal scoring controls survive fixture serialization");
            Check(SameUnityObject(GetPrivate(targetA, "_outdoorMixerParams"), policyA.OutdoorMixerParams) &&
                SameUnityObject(GetPrivate(targetA, "_indoorMixerParams"), policyA.IndoorMixerParams) &&
                SameUnityObject(GetPrivate(targetB, "_outdoorMixerParams"), policyB.OutdoorMixerParams) &&
                SameUnityObject(GetPrivate(targetB, "_indoorMixerParams"), policyB.IndoorMixerParams),
                "Target blender mixer PPtrs round-trip beside the policy references");
            var targetSystemBlenders = GetPrivate(targetSystem, "_ambientSoundBlenders") as AmbientSoundBlender[];
            if (targetSystemBlenders == null) throw new InvalidDataException("Target environment system blender PPtr array is missing");
            Check(targetSystemBlenders.Length == 2 &&
                SameUnityObject(targetSystemBlenders[0], targetA) &&
                SameUnityObject(targetSystemBlenders[1], targetB),
                "Target environment system preserves both blender PPtrs");
            CheckCurveRoundtrip(policyA.VolumeCurve, GetPrivate(targetA, "_volumeCurve") as AnimationCurve,
                "First blender volume curve", Check);
            CheckCurveRoundtrip(policyA.OcclusionCurve, GetPrivate(targetA, "_occlusionCurve") as AnimationCurve,
                "First blender occlusion curve", Check);
            CheckCurveRoundtrip(policyB.VolumeCurve, GetPrivate(targetB, "_volumeCurve") as AnimationCurve,
                "Second blender volume curve", Check);
            CheckCurveRoundtrip(policyB.OcclusionCurve, GetPrivate(targetB, "_occlusionCurve") as AnimationCurve,
                "Second blender occlusion curve", Check);
            CheckMixerLayout(policyA.OutdoorMixerParams as AudioMixerParametersData,
                new[] { "AmbientOutVolume" }, new[] { "AmbientOutLowpass", "AmbientEffectsLowpass" },
                new[] { "AmbientOutHighpass" }, new[] { "AmbientOutReverbSend" }, new[] { "AmbientOutDelaySend" }, Check);
            CheckMixerLayout(policyA.IndoorMixerParams as AudioMixerParametersData,
                new[] { "AmbientInVolume" }, Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<string>(), Check);

            // Run scoring and graph checks against a private, unregistered
            // binding before the fixture is activated. This keeps the probe's
            // authored system coroutine isolated from the deterministic cases.
            scoreBinding = new AmbientBlendBindings.SystemBinding(systemPolicy, targetSystem);
            RunScoreChecks(scoreBinding, systemPolicy, Check);
            graphScene = SceneManager.CreateScene("MI_AmbientBlendNeighborValidation");
            await RunNeighborChecks(scoreBinding, graphScene, Check, token);
            neighborChecksRan = true;

            root.SetActive(true);
            await UniTask.Yield();
            await UniTask.Yield();
            Check(AmbientBlendBindings.BlenderCount == beforeBlenderCount + input.OwnedBlenders,
                "Ambient policy lifecycle registers exactly two owned blenders");
            Check(AmbientBlendBindings.SystemCount == beforeSystemCount + input.OwnedSystems,
                "Ambient policy lifecycle registers exactly one owned system");

            Check(AmbientBlendBindings.BlenderBindings.TryGetValue(policyA, out blenderA),
                "First owned blender binding is addressable");
            Check(AmbientBlendBindings.BlenderBindings.TryGetValue(policyB, out blenderB),
                "Second owned blender binding is addressable");
            Check(AmbientBlendBindings.SystemBindings.TryGetValue(systemPolicy, out ownedSystem),
                "Owned system binding is addressable");
            if (blenderA == null || blenderB == null || ownedSystem == null)
                throw new InvalidDataException("Ambient policy bindings disappeared after registration");
            var controlBlender = root.transform.Find("UnownedBlenderControl")?.GetComponent<AmbientSoundBlender>();
            var controlSystem = root.transform.Find("UnownedEnvironmentControl")?.GetComponent<EnvironmentSoundBlendSystem>();
            Check(controlBlender != null && controlSystem != null && !HasBlenderBinding(controlBlender) &&
                !AmbientBlendBindings.TryInitializeSystem(controlSystem),
                "Unowned target controls remain outside the adapter scope");

            if (MonoBehaviourSingleton<BetterAudio>.Instantiated)
            {
                var betterAudio = MonoBehaviourSingleton<BetterAudio>.Instance;
                if (betterAudio != null) master = betterAudio.Master;
            }
            if (master == null)
            {
                // Target globalgamemanagers maps this Resources key to the
                // installed MasterMixer at resources.assets:4340. Load the
                // real mixer without creating a synthetic BetterAudio owner.
                master = Resources.Load<AudioMixer>("audio/mastermixer");
                Check(master != null && master.name == "MasterMixer",
                    "Installed raid MasterMixer resolves through its native Resources key");
            }
            if (master != null)
            {
                if (blenderA!.Master == null) blenderA.Initialize(master);
                if (blenderB!.Master == null) blenderB.Initialize(master);
                if (!blenderA.Initialized || !blenderB.Initialized)
                {
                    throw new InvalidDataException("Ambient bindings did not initialize against the native MasterMixer");
                }
                else
                {
                    await RunMixerChecks(blenderA, blenderB, master, mixerRestore, Check, token);
                    mixerChecksRan = true;
                }
            }
        }
        finally
        {
            StopFader(blenderA?.OutdoorFader);
            StopFader(blenderA?.IndoorFader);
            StopFader(blenderB?.OutdoorFader);
            StopFader(blenderB?.IndoorFader);
            RestoreMixer(master, mixerRestore);
            scoreBinding?.Dispose();
            if (graphScene.IsValid() && graphScene.isLoaded)
            {
                var unloadGraph = SceneManager.UnloadSceneAsync(graphScene);
                if (unloadGraph != null) await UniTask.WaitUntil(() => unloadGraph.isDone);
            }
            if (scene.IsValid() && scene.isLoaded)
            {
                var unload = SceneManager.UnloadSceneAsync(scene);
                if (unload != null) await UniTask.WaitUntil(() => unload.isDone);
            }
            if (bundle != null) bundle.Unload(true);
            await UniTask.Yield();
            Application.logMessageReceived -= CaptureUnityError;
        }

        Check(AmbientBlendBindings.BlenderCount == beforeBlenderCount,
            "Ambient scene unload removes both owned blender bindings");
        Check(AmbientBlendBindings.SystemCount == beforeSystemCount,
            "Ambient scene unload removes the owned system binding");
        Check(SceneManager.sceneCount == beforeSceneCount, "Ambient probe restores the original scene count");
        Check(blenderA == null || blenderA.Disposed, "First blender fader resources are disposed on scene unload");
        Check(blenderB == null || blenderB.Disposed, "Second blender fader resources are disposed on scene unload");
        Check(ownedSystem == null || ownedSystem.Blenders.Count == 0, "System binding releases its blender list on unload");
        Check(unityErrors.Count == 0, "No Unity errors during ambient fixture lifecycle" +
            (unityErrors.Count == 0 ? "" : ": " + string.Join("; ", unityErrors)));

        return new
        {
            Passed = true,
            BundleSha256 = input.Sha256,
            Checks = checks,
            Deferred = deferred,
            UnityErrors = unityErrors,
            MixerChecksRan = mixerChecksRan,
            NeighborChecksRan = neighborChecksRan,
            OwnedBlenders = input.OwnedBlenders,
            OwnedSystems = input.OwnedSystems,
            Scope = "Two source ambient blenders and one environment system: target mixer readback/fader transition when BetterAudio is available, independent source portal scoring, final versus in-progress portal neighbors, ownership boundaries, and scene cleanup."
        };
    }

    private static AmbientBlendPolicy FindPolicy(AmbientBlendPolicy[] policies, string sourceKey)
    {
        for (var i = 0; i < policies.Length; i++)
            if (string.Equals(policies[i].SourceKey, sourceKey, StringComparison.Ordinal)) return policies[i];
        throw new InvalidDataException("Ambient fixture policy missing: " + sourceKey);
    }

    private static bool HasBlenderBinding(AmbientSoundBlender target)
    {
        foreach (var binding in AmbientBlendBindings.BlenderBindings.Values)
            if (ReferenceEquals(binding.Target, target)) return true;
        return false;
    }

    private static bool SameUnityObject(object? leftValue, object? rightValue)
    {
        var left = leftValue as UnityEngine.Object;
        var right = rightValue as UnityEngine.Object;
        return left == right;
    }

    private static void CheckCurveRoundtrip(AnimationCurve? expected, AnimationCurve? actual,
        string name, Action<bool, string> check)
    {
        if (expected == null || actual == null) throw new InvalidDataException("Ambient curve missing: " + name);
        var expectedKeys = expected.keys;
        var actualKeys = actual.keys;
        var equal = expected.preWrapMode == actual.preWrapMode && expected.postWrapMode == actual.postWrapMode &&
            expectedKeys.Length == actualKeys.Length;
        if (equal)
        {
            for (var i = 0; i < expectedKeys.Length; i++)
            {
                var left = expectedKeys[i];
                var right = actualKeys[i];
                if (Mathf.Abs(left.time - right.time) > .000001f ||
                    Mathf.Abs(left.value - right.value) > .000001f ||
                    Mathf.Abs(left.inTangent - right.inTangent) > .000001f ||
                    Mathf.Abs(left.outTangent - right.outTangent) > .000001f ||
                    left.weightedMode != right.weightedMode ||
                    Mathf.Abs(left.inWeight - right.inWeight) > .000001f ||
                    Mathf.Abs(left.outWeight - right.outWeight) > .000001f)
                {
                    equal = false;
                    break;
                }
            }
        }
        check(equal, name + " round-trips keyframes, tangents, weights and wrap modes");
    }

    private static void CheckMixerLayout(AudioMixerParametersData? data, string[] volume, string[] lowPass,
        string[] highPass, string[] reverb, string[] delay, Action<bool, string> check)
    {
        if (data == null) throw new InvalidDataException("Ambient mixer parameter asset is missing");
        CheckParameters(data, EMixerParameterType.Volume, volume, check);
        CheckParameters(data, EMixerParameterType.LowPassFreq, lowPass, check);
        CheckParameters(data, EMixerParameterType.HighPassFreq, highPass, check);
        CheckParameters(data, EMixerParameterType.ReverbSend, reverb, check);
        CheckParameters(data, EMixerParameterType.DelaySend, delay, check);
    }

    private static string[] GetParameters(AudioMixerParametersData data, EMixerParameterType type)
    {
        if (!data.TryGetParameters(type, out var container) || container == null)
            return Array.Empty<string>();
        var result = new string[container.Count];
        for (var i = 0; i < result.Length; i++) result[i] = container[i];
        return result;
    }

    private static void CheckParameters(AudioMixerParametersData data, EMixerParameterType type,
        string[] expected, Action<bool, string> check)
    {
        var actual = GetParameters(data, type);
        if (actual.Length != expected.Length) throw new InvalidDataException("Ambient mixer parameter count differs for " + type);
        for (var i = 0; i < expected.Length; i++)
            check(string.Equals(actual[i], expected[i], StringComparison.Ordinal),
                "Authored ambient mixer parameter preserved: " + expected[i]);
    }

    private static void RunScoreChecks(AmbientBlendBindings.SystemBinding binding, AmbientBlendPolicy policy,
        Action<bool, string> check)
    {
        var oldHeightWeight = policy.ListenerHeightWeight;
        var oldDepthMult = policy.DepthCalculationMult;
        var oldConnectedPenalty = policy.ConnectedRoomPenalty;
        var oldNeighborPenalty = policy.NeighborRoomPenalty;
        var oldIgnoreRatio = policy.IgnoreDistanceRatio;
        try
        {
            policy.ListenerHeightWeight = .5f;
            policy.DepthCalculationMult = 2.7f;
            policy.ConnectedRoomPenalty = .3f;
            policy.NeighborRoomPenalty = .4f;
            policy.IgnoreDistanceRatio = false;
            SetPrivate(binding, "_currentIndoorRoomID", 42);
            var listener = Vector3.zero;

            var connected = Portal(101, new Vector3(1f, 2f, 0f), 4f, 0, 10f, 0, 1);
            SetPrivate(binding, "_currentIndoorRoomsIDs", new short[] { 42 });
            SetPrivate(binding, "_currentNeighborRoomIDs", Array.Empty<short>());
            SetPrivate(binding, "_currentOutPortalsData", new[] { connected });
            CheckScoreCase(binding, policy, listener, new[] { connected }, new short[] { 42 }, Array.Empty<short>(),
                "Connected open portal score and adjusted distance match independent source formula", check);

            var closed = Portal(102, new Vector3(2f, 0f, 0f), 3f, 1, 10f, 0, 1);
            CheckNoPortalCase(binding, new[] { closed }, new short[] { 42 }, Array.Empty<short>(), listener,
                "Final closed portal is rejected by the source score state predicate", check);

            var inProgressOpen = Portal(103, new Vector3(1f, 0f, 0f), 1f, 2, 10f, 0, 1);
            CheckScoreCase(binding, policy, listener, new[] { inProgressOpen }, new short[] { 42 }, Array.Empty<short>(),
                "In-progress-open portal remains eligible for ambient scoring", check);
            var inProgressClose = Portal(104, new Vector3(1f, 0f, 0f), 1f, 3, 10f, 0, 1);
            CheckNoPortalCase(binding, new[] { inProgressClose }, new short[] { 42 }, Array.Empty<short>(), listener,
                "In-progress-close portal is excluded from ambient scoring", check);

            var outOfRange = Portal(105, new Vector3(28f, 0f, 0f), 1f, 0, 10f, 0, 1);
            CheckNoPortalCase(binding, new[] { outOfRange }, new short[] { 42 }, Array.Empty<short>(), listener,
                "Portal outside depth multiplier range is excluded", check);
            var invalidDepth = Portal(106, new Vector3(1f, 0f, 0f), 1f, 0, 0f, 0, 1);
            CheckNoPortalCase(binding, new[] { invalidDepth }, new short[] { 42 }, Array.Empty<short>(), listener,
                "Zero-depth portal is excluded", check);

            var neighbor = Portal(107, new Vector3(1f, 0f, 0f), 2f, 0, 10f, 0, 1);
            CheckScoreCase(binding, policy, listener, new[] { neighbor }, new short[] { 43 }, new short[] { 43 },
                "Neighbor portal penalty and acceptance match independent source formula", check);
            policy.IgnoreDistanceRatio = true;
            CheckScoreCase(binding, policy, listener, new[] { neighbor }, new short[] { 43 }, new short[] { 43 },
                "IgnoreDistanceRatio changes only adjusted distance, not the diffraction score", check);
            policy.IgnoreDistanceRatio = false;

            var lateConnected = Portal(108, new Vector3(1f, 0f, 0f), 2f, 0, 10f, 0, 2);
            CheckScoreCase(binding, policy, listener, new[] { lateConnected }, new short[] { 43, 42 }, new short[] { 43 },
                "Late room match uses the first portal room for neighbor acceptance", check);
            CheckNoPortalCase(binding, new[] { lateConnected }, new short[] { 44, 42 }, new short[] { 43 }, listener,
                "Late room match rejects a portal whose first room is not a neighbor", check);

            var tieA = Portal(109, new Vector3(1f, 0f, 0f), 2f, 0, 10f, 0, 1);
            var tieB = Portal(110, new Vector3(1f, 0f, 0f), 1f, 0, 10f, 0, 1);
            CheckScoreCase(binding, policy, listener, new[] { tieA, tieB }, new short[] { 42, 42 }, Array.Empty<short>(),
                "Approximately equal portal scores use traversal cost as the tie breaker", check);

            var filter = new AmbientBlendBindings.BlenderBinding(policy, policy.GetComponent<AmbientSoundBlender>()!);
            check(Mathf.Abs(filter.CalculateOutdoorFilterValue(10f, -1f, 2.7f) - 1f) < .000001f,
                "Outdoor filter clamps the near boundary to one");
            check(Mathf.Abs(filter.CalculateOutdoorFilterValue(10f, 27f, 2.7f)) < .000001f,
                "Outdoor filter reaches zero at the depth boundary");
            check(Mathf.Abs(filter.CalculateOutdoorFilterValue(10f, 28f, 2.7f)) < .000001f,
                "Outdoor filter clamps beyond the depth boundary to zero");
        }
        finally
        {
            policy.ListenerHeightWeight = oldHeightWeight;
            policy.DepthCalculationMult = oldDepthMult;
            policy.ConnectedRoomPenalty = oldConnectedPenalty;
            policy.NeighborRoomPenalty = oldNeighborPenalty;
            policy.IgnoreDistanceRatio = oldIgnoreRatio;
        }
    }

    private static void CheckScoreCase(AmbientBlendBindings.SystemBinding binding, AmbientBlendPolicy policy,
        Vector3 listener, PortalData[] portals, short[] indoorRooms, short[] neighbors, string name,
        Action<bool, string> check)
    {
        SetPrivate(binding, "_currentIndoorRoomsIDs", indoorRooms);
        SetPrivate(binding, "_currentNeighborRoomIDs", neighbors);
        SetPrivate(binding, "_currentOutPortalsData", portals);
        var actual = InvokeOptimal(binding, listener);
        var expected = IndependentOptimal(policy, listener, portals, indoorRooms, neighbors, 42);
        check(actual.portalData.id == expected.portalData.id &&
            Mathf.Abs(actual.score - expected.score) < .00001f &&
            Mathf.Abs(actual.distanceToListener - expected.distanceToListener) < .00001f, name);
    }

    private static void CheckNoPortalCase(AmbientBlendBindings.SystemBinding binding, PortalData[] portals,
        short[] indoorRooms, short[] neighbors, Vector3 listener, string name, Action<bool, string> check)
    {
        SetPrivate(binding, "_currentIndoorRoomsIDs", indoorRooms);
        SetPrivate(binding, "_currentNeighborRoomIDs", neighbors);
        SetPrivate(binding, "_currentOutPortalsData", portals);
        var actual = InvokeOptimal(binding, listener);
        check(actual.portalData.id == 0, name);
    }

    private static PortalData Portal(int id, Vector3 position, float cost, byte state, float depth,
        short startIndex, short roomCount)
        => new PortalData(id, position, Vector3.forward, cost, state, depth, startIndex, roomCount);

    private static PortalCalculatedData IndependentOptimal(AmbientBlendPolicy policy, Vector3 listener,
        PortalData[] portals, short[] indoorRooms, short[] neighbors, short currentRoom)
    {
        var best = PortalCalculatedData.Default();
        var found = false;
        var bestScore = float.MaxValue;
        var bestCost = float.MaxValue;
        for (var i = 0; i < portals.Length; i++)
        {
            var portal = portals[i];
            // Native IsPortalStateValid rejects final closed (1) and
            // in-progress close (3); in-progress open (2) is still scored.
            if (portal.state == 1 || portal.state == 3 || portal.depth <= 0f ||
                float.IsNaN(portal.depth) || float.IsInfinity(portal.depth)) continue;
            var distance = Vector3.Distance(listener, portal.position);
            if (distance > portal.depth * policy.DepthCalculationMult) continue;
            if (!IndependentAccept(portal, indoorRooms, neighbors, currentRoom, out var connected, out var neighbor)) continue;
            var ratio = distance / portal.depth;
            var horizontal = SoundOcclusionUtils.CalculateHorizontalDiffraction(listener, portal.position);
            // IgnoreDistanceRatio only changes the adjusted-distance penalty;
            // the score always interpolates diffraction by distance/depth.
            var horizontalScore = Mathf.Lerp(0f, horizontal, ratio);
            var verticalScore = Mathf.Abs(listener.y - portal.position.y) * policy.ListenerHeightWeight;
            var score = ratio + horizontalScore + verticalScore;
            if (!connected) score += policy.ConnectedRoomPenalty;
            if (neighbor) score += policy.NeighborRoomPenalty;
            if (!found || score < bestScore ||
                (Mathf.Abs(score - bestScore) < .05f && portal.cost < bestCost))
            {
                found = true;
                bestScore = score;
                bestCost = portal.cost;
                var distanceWeight = policy.IgnoreDistanceRatio ? 1f : ratio;
                var adjustedDistance = distance * (1f +
                    ((!connected ? policy.ConnectedRoomPenalty : 0f) +
                     (neighbor ? policy.NeighborRoomPenalty : 0f)) * distanceWeight);
                best = new PortalCalculatedData(score, adjustedDistance, portal);
            }
        }
        return best;
    }

    private static bool IndependentAccept(PortalData portal, short[] indoorRooms, short[] neighbors,
        short currentRoom, out bool connected, out bool neighbor)
    {
        connected = false;
        neighbor = false;
        var start = portal.startIndex;
        var count = portal.indoorRoomsCount;
        if (start < 0 || count < 0 || start + count > indoorRooms.Length) return false;
        for (var i = 0; i < count; i++)
        {
            var roomID = indoorRooms[start + i];
            if (roomID != currentRoom) continue;
            if (i == 0)
            {
                connected = true;
                return true;
            }
            if (Contains(neighbors, indoorRooms[start]))
            {
                neighbor = true;
                return true;
            }
            return false;
        }
        for (var i = 0; i < count; i++)
        {
            if (!Contains(neighbors, indoorRooms[start + i])) continue;
            neighbor = true;
            return true;
        }
        return false;
    }

    private static bool Contains(short[] values, short value)
    {
        for (var i = 0; i < values.Length; i++) if (values[i] == value) return true;
        return false;
    }

    private static AmbientPortalCalculated InvokeOptimal(AmbientBlendBindings.SystemBinding binding, Vector3 listener)
    {
        var method = AccessTools.Method(typeof(AmbientBlendBindings.SystemBinding), "CalculateOptimalPortal")
            ?? throw new MissingMethodException(typeof(AmbientBlendBindings.SystemBinding).FullName, "CalculateOptimalPortal");
        return new AmbientPortalCalculated((PortalCalculatedData)method.Invoke(binding, new object[] { listener })!);
    }

    private readonly struct AmbientPortalCalculated
    {
        internal readonly PortalCalculatedData Value;
        internal AmbientPortalCalculated(PortalCalculatedData value) { Value = value; }
        internal int Id => Value.portalData.id;
        internal float Score => Value.score;
        internal float Distance => Value.distanceToListener;
        internal PortalData portalData => Value.portalData;
        internal float score => Value.score;
        internal float distanceToListener => Value.distanceToListener;
    }

    private static async UniTask RunNeighborChecks(AmbientBlendBindings.SystemBinding binding, Scene graphScene,
        Action<bool, string> check, CancellationToken token)
    {
        var root = new GameObject("MI_AmbientNeighborGraph");
        root.SetActive(false);
        SceneManager.MoveGameObjectToScene(root, graphScene);
        var room = NewRoom(root.transform, "ListenerRoom", 700);
        var openNeighbor = NewRoom(root.transform, "OpenNeighbor", 701);
        var closedNeighbor = NewRoom(root.transform, "ClosedNeighbor", 702);
        var openingNeighbor = NewRoom(root.transform, "OpeningNeighbor", 703);
        var closingNeighbor = NewRoom(root.transform, "ClosingNeighbor", 704);
        var open = NewPortal(root.transform, "OpenPortal", 710, BaseSpatialAudioPortal.PortalState.Open, openNeighbor);
        var closed = NewPortal(root.transform, "ClosedPortal", 711, BaseSpatialAudioPortal.PortalState.Closed, closedNeighbor);
        var opening = NewPortal(root.transform, "OpeningPortal", 712, BaseSpatialAudioPortal.PortalState.InProgressOpen, openingNeighbor);
        var closing = NewPortal(root.transform, "ClosingPortal", 713, BaseSpatialAudioPortal.PortalState.InProgressClose, closingNeighbor);
        room.allPortals = new List<ISpatialPortal> { open, closed, opening, closing };

        var method = AccessTools.Method(typeof(AmbientBlendBindings.SystemBinding), "TryFillOpenNeighborRoomIDs")
            ?? throw new MissingMethodException(typeof(AmbientBlendBindings.SystemBinding).FullName, "TryFillOpenNeighborRoomIDs");
        var buffer = (List<short>)GetPrivate(binding, "_neighborBuffer")!;
        buffer.Clear();
        var found = (bool)method.Invoke(binding, new object[] { room })!;
        check(found && buffer.Count == 2 && buffer[0] == 701 && buffer[1] == 702,
            "Final open/closed portals contribute unique neighbor rooms");
        buffer.Clear();
        open.state = BaseSpatialAudioPortal.PortalState.InProgressOpen;
        closed.state = BaseSpatialAudioPortal.PortalState.InProgressClose;
        found = (bool)method.Invoke(binding, new object[] { room })!;
        check(found && buffer.Count == 0, "In-progress opening/closing portals are excluded from neighbor rooms");
        open.state = BaseSpatialAudioPortal.PortalState.Open;
        closed.state = BaseSpatialAudioPortal.PortalState.Closed;
        buffer.Clear();
        found = (bool)method.Invoke(binding, new object[] { room })!;
        check(found && buffer.Count == 2, "Neighbor graph refresh restores final portal states");
        token.ThrowIfCancellationRequested();
        await UniTask.Yield();
    }

    private static SpatialAudioRoom NewRoom(Transform parent, string name, short id)
    {
        var objectRoot = new GameObject(name);
        objectRoot.transform.SetParent(parent, false);
        objectRoot.SetActive(false);
        var room = objectRoot.AddComponent<SpatialAudioRoom>();
        SetPrivate(room, "_iD", id);
        room.allPortals = new List<ISpatialPortal>();
        return room;
    }

    private static SpatialAudioPortal NewPortal(Transform parent, string name, short id,
        BaseSpatialAudioPortal.PortalState state, SpatialAudioRoom connectedRoom)
    {
        var objectRoot = new GameObject(name);
        objectRoot.transform.SetParent(parent, false);
        objectRoot.SetActive(false);
        var portal = objectRoot.AddComponent<SpatialAudioPortal>();
        SetPrivate(portal, "_iD", id);
        SetPrivate(portal, "_connectedRooms", new List<SpatialAudioRoom> { connectedRoom });
        portal.state = state;
        return portal;
    }

    private static async UniTask RunMixerChecks(AmbientBlendBindings.BlenderBinding blenderA,
        AmbientBlendBindings.BlenderBinding blenderB, AudioMixer master, Dictionary<string, float> restore,
        Action<bool, string> check, CancellationToken token)
    {
        CaptureMixerValues(blenderA.Policy.OutdoorMixerParams as AudioMixerParametersData, master, restore);
        CaptureMixerValues(blenderA.Policy.IndoorMixerParams as AudioMixerParametersData, master, restore);
        if (restore.Count == 0) throw new InvalidDataException("Ambient native mixer exposes no fixture parameters");

        var cases = new[] { -0.1f, 0f, .25f, .75f, 1f, 1.1f };
        for (var i = 0; i < cases.Length; i++)
        {
            var value = cases[i];
            const float maxOutdoorVolume = .25f;
            const float maxOutdoorFreq = 9000f;
            blenderA.SetOutdoor(value, maxOutdoorVolume, maxOutdoorFreq, .5f, true, null);
            CheckOutdoorReadback(blenderA.Policy, master, value, maxOutdoorVolume, maxOutdoorFreq, check,
                "Native mixer readback at source boundary " + value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            token.ThrowIfCancellationRequested();
        }

        blenderA.SetIndoor(1f, .5f, true, blenderA.Policy.FadeInType);
        CheckReadback(master, GetParameters((AudioMixerParametersData)blenderA.Policy.IndoorMixerParams,
            EMixerParameterType.Volume), 0f, check, "Indoor native mixer reaches zero dB at full value");

        blenderB.SetOutdoor(.2f, .2f, 12000f, .5f, true, null);
        var volumeParameter = FirstParameter(blenderB.Policy.OutdoorMixerParams as AudioMixerParametersData, EMixerParameterType.Volume);
        if (!master.GetFloat(volumeParameter, out var startValue))
            throw new InvalidDataException("Ambient native volume parameter cannot be read before fader test");
        blenderB.SetOutdoor(.8f, .2f, 12000f, 1f, false, blenderB.Policy.FadeOutType);
        check(blenderB.OutdoorFader != null && blenderB.OutdoorFader.IsFading,
            "Native AudioMixerFader starts the authored outdoor transition");
        await UniTask.Delay(100, ignoreTimeScale: true, cancellationToken: token);
        if (!master.GetFloat(volumeParameter, out var midValue))
            throw new InvalidDataException("Ambient native volume parameter cannot be read during fader test");
        check(Mathf.Abs(midValue - startValue) > .00001f && blenderB.OutdoorFader != null && blenderB.OutdoorFader.IsFading,
            "Native outdoor fader changes the mixer during its transition");
        blenderB.OutdoorFader!.StopFade();
        check(!blenderB.OutdoorFader.IsFading, "Native outdoor fader stops cleanly");
    }

    private static void CheckOutdoorReadback(AmbientBlendPolicy policy, AudioMixer master, float value,
        float maxOutdoorVolume, float maxOutdoorFreq, Action<bool, string> check, string label)
    {
        var occlusion = policy.OcclusionCurve.Evaluate(value);
        var clampedOcclusion = Mathf.Clamp01(occlusion);
        var normalizedVolume = Mathf.Clamp(Mathf.Max(policy.VolumeCurve.Evaluate(value), maxOutdoorVolume), .0001f, 1f);
        var volumeDb = EFT.AudioUtils.ConvertNormalizedVolumeToDB(normalizedVolume);
        var cutoff = EFT.AudioUtils.ConvertNormalizedValueToCutoffFrequency(occlusion);
        var outdoorFilter = Mathf.Clamp01(value);
        var lowpass = Mathf.Max(policy.MinLowpassFreq,
            Mathf.Lerp(maxOutdoorFreq, policy.MaxLowpassFreq, outdoorFilter));
        cutoff = Mathf.Clamp(cutoff, lowpass, policy.MaxLowpassFreq);
        var highpass = Mathf.Lerp(policy.MinHighPassFreq, policy.MaxHighPassFreq, clampedOcclusion);
        var reverb = Mathf.Clamp(volumeDb, -80f, policy.MaxOutdoorReverbSendDb);
        var delay = Mathf.Clamp(volumeDb, -80f, policy.MaxOutdoorDelaySendDb);
        CheckReadback(master, GetParameters((AudioMixerParametersData)policy.OutdoorMixerParams, EMixerParameterType.Volume), volumeDb, check, label + " volume");
        CheckReadback(master, GetParameters((AudioMixerParametersData)policy.OutdoorMixerParams, EMixerParameterType.LowPassFreq), cutoff, check, label + " low-pass");
        CheckReadback(master, GetParameters((AudioMixerParametersData)policy.OutdoorMixerParams, EMixerParameterType.HighPassFreq), highpass, check, label + " high-pass");
        CheckReadback(master, GetParameters((AudioMixerParametersData)policy.OutdoorMixerParams, EMixerParameterType.ReverbSend), reverb, check, label + " reverb send");
        CheckReadback(master, GetParameters((AudioMixerParametersData)policy.OutdoorMixerParams, EMixerParameterType.DelaySend), delay, check, label + " delay send");
    }

    private static void CheckReadback(AudioMixer master, string[] parameters, float expected,
        Action<bool, string> check, string name)
    {
        if (parameters.Length == 0) throw new InvalidDataException("Ambient fixture has no mixer parameter for " + name);
        for (var i = 0; i < parameters.Length; i++)
        {
            if (!master.GetFloat(parameters[i], out var actual))
                throw new InvalidDataException("Target mixer parameter is not exposed: " + parameters[i]);
            check(Mathf.Abs(actual - expected) < .02f, name + " readback");
        }
    }

    private static string FirstParameter(AudioMixerParametersData? data, EMixerParameterType type)
    {
        if (data == null) throw new InvalidDataException("Ambient mixer data missing for fader test");
        var values = GetParameters(data, type);
        if (values.Length == 0) throw new InvalidDataException("Ambient mixer parameter missing for fader test");
        return values[0];
    }

    private static void CaptureMixerValues(AudioMixerParametersData? data, AudioMixer master,
        Dictionary<string, float> restore)
    {
        if (data == null) throw new InvalidDataException("Ambient mixer data missing");
        var types = new[] { EMixerParameterType.Volume, EMixerParameterType.LowPassFreq,
            EMixerParameterType.HighPassFreq, EMixerParameterType.ReverbSend, EMixerParameterType.DelaySend };
        for (var i = 0; i < types.Length; i++)
        {
            var values = GetParameters(data, types[i]);
            for (var j = 0; j < values.Length; j++)
            {
                if (!master.GetFloat(values[j], out var current))
                    throw new InvalidDataException("Target mixer parameter is not exposed: " + values[j]);
                if (!restore.ContainsKey(values[j])) restore.Add(values[j], current);
            }
        }
    }

    private static void RestoreMixer(AudioMixer? master, Dictionary<string, float> values)
    {
        if (master == null) return;
        foreach (var entry in values) master.SetFloat(entry.Key, entry.Value);
    }

    private static void StopFader(IAudioMixerFader? fader)
    {
        if (fader == null) return;
        if (fader.IsFading) fader.StopFade();
    }

    private static object? GetPrivate(object target, string name)
    {
        var field = AccessTools.Field(target.GetType(), name)
            ?? throw new MissingFieldException(target.GetType().FullName, name);
        return field.GetValue(target);
    }

    private static void SetPrivate(object target, string name, object value)
    {
        var field = AccessTools.Field(target.GetType(), name)
            ?? throw new MissingFieldException(target.GetType().FullName, name);
        field.SetValue(target, value);
    }
}
