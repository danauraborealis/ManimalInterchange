using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using EFT;
using EFT.CameraControl;
using HarmonyLib;
using Manimal.Interchange.Components;
using Manimal.Interchange.Shared;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZLinq;
using Effect = Systems.Effects.Effects.Effect;

namespace Manimal.Interchange.Client;

internal static class AudioPolicyProbe
{
    private sealed class Input { public string Bundle = ""; public string Sha256 = ""; public string Scene = ""; }
    internal static async UniTask<object> Run(CancellationToken token)
    {
        var input = JsonConvert.DeserializeObject<Input>(File.ReadAllText(Path.Combine(Plugin.Root, "audio-validation.json")))
            ?? throw new InvalidDataException("Audio fixture manifest missing");
        var path = ManifestRules.Resolve(Plugin.Root, input.Bundle);
        if (ManifestRules.Hash(path) != input.Sha256) throw new InvalidDataException("Audio fixture changed");
        var checks = new List<string>();
        var errors = new List<string>();
        var clipHashes = new List<string>();
        var playbackTested = false;
        void Check(bool pass, string name) { token.ThrowIfCancellationRequested(); if (!pass) throw new InvalidDataException(name); checks.Add(name); }
        void Error(string message, string stack, LogType kind) { if (kind is LogType.Error or LogType.Exception or LogType.Assert) errors.Add(message); }
        var counts = (AudioBindings.Catalogs.Count, AudioBindings.Policies.Count, AudioBindings.Blends.Count);
        var scenes = SceneManager.sceneCount;
        AssetBundle? bundle = null;
        Scene scene = default;
        var playing = new HashSet<BetterSource>();
        AudioBindings.Binding? binding = null;
        var gags = new List<Gag>();
        Application.logMessageReceived += Error;
        try
        {
            var request = AssetBundle.LoadFromFileAsync(path);
            await UniTask.WaitUntil(() => request.isDone);
            bundle = request.assetBundle;
            if (!bundle) throw new InvalidDataException("Audio fixture failed to load");
            Check(bundle.GetAllScenePaths().AsValueEnumerable().SequenceEqual(new[] { input.Scene }), "Exact audio fixture scene");
            var load = SceneManager.LoadSceneAsync(input.Scene, LoadSceneMode.Additive);
            await UniTask.WaitUntil(() => load.isDone);
            scene = SceneManager.GetSceneByPath(input.Scene);
            var roots = scene.GetRootGameObjects();
            Check(roots.Length == 1 && !roots[0].activeSelf, "Audio fixture remains inactive until validation");
            var catalog = roots[0].GetComponent<AudioCatalog>();
            var banks = catalog.Banks.AsValueEnumerable().Cast<SoundBank>().ToArray();
            Check(banks.Length == 4 && catalog.LimitedBanks.Length == 3 && catalog.BlendProfiles.Length == 1, "Native bank, blend and catalog references deserialized");
            Check(catalog.LimitRadius[0] == new Vector2(.5f, 1.5f) && catalog.LimitPercentOfClip[1] == .6f && !catalog.LimitIfVisible[0], "Exact authored limiter variants deserialized");
            roots[0].SetActive(true);
            Check(AudioBindings.Catalogs.Count == counts.Item1 + 1 && AudioBindings.Policies.Count == counts.Item2 + 3 && AudioBindings.Blends.Count == counts.Item3 + 1, "Natural Awake registers only fixture resource identities");
            binding = AudioBindings.Catalogs[catalog];
            await UniTask.WaitUntil(() => binding.Clips.AsValueEnumerable().All(c => c.loadState == AudioDataLoadState.Loaded));
            Check(binding.Loaded && binding.Clips.Count == 3, "Native audio preloading loads each shared clip once");
            binding.Load();
            Check(binding.Clips.Count == 3, "Repeated load is idempotent");
            foreach (var clip in binding.Clips)
            {
                var samples = new float[clip.samples * clip.channels];
                Check(clip.frequency == 48000 && clip.channels == 1 && clip.GetData(samples, 0) && samples.AsValueEnumerable().Any(v => v != 0), "Native PCM data available: " + clip.name);
                var bytes = new byte[samples.Length * 4]; Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                using var sha = System.Security.Cryptography.SHA256.Create();
                clipHashes.Add(BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant());
            }
            var samplesToTest = new[] { (0f, 1f), (5f, 1f), (13.75f, .875f), (22.5f, .5f), (31.25f, .125f), (40f, 1f),
                (50f, 1f), (60f, 1f), (70f, .875f), (80f, .5f), (90f, .125f), (100f, 1f), (101f, 1f) };
            foreach (var sample in samplesToTest)
            {
                AudioClip a = null!, b = null!; float proportion = -123;
                banks[0].PickClipsByDistance(ref a, ref b, ref proportion, 0, sample.Item1);
                var band = sample.Item1 < 40 ? 0 : sample.Item1 < 100 ? 1 : 2;
                Check(Mathf.Abs(proportion - sample.Item2) < .000001f && a == banks[0].Environments[0].Clips[band].Clips[0], "Native clip selection and retail fade at distance " + sample.Item1);
            }
            AudioClip controlA = null!, controlB = null!; float controlMix = 0;
            banks[3].PickClipsByDistance(ref controlA, ref controlB, ref controlMix, 0, 13.75f);
            Check(controlMix == .75f && !AudioBindings.Policies.ContainsKey(banks[3]), "Unowned bank retains native linear blend and no limiter policy");
            var policy = AudioBindings.Policies[banks[0]];
            Check(!policy.ShouldLimit(true, true, false) && policy.ShouldLimit(true, false, false), "Visibility bypass matches the source policy");
            Check(policy.ShouldLimit(false, true, true) && !policy.ShouldLimit(false, false, false), "Melee and disabled-bank limiter policy preserved");
            Check(AudioBindings.Policies[banks[1]].ShouldLimit(true, true, false), "LimitIfVisible explicitly limits visible effects");
            var center = Vector3.zero; var listener = new Vector3(0, 0, -10);
            var gag = new Gag { Position = center, Radius = new Vector2(1, 3), Rolloff = 100, EndTime = double.MaxValue };
            var nativeControlBefore = gag.IsChoked(new Vector3(.6f, 0, 0), listener, 25);
            AudioBindings.OwnedGags.Add(gag, binding); gags.Add(gag);
            Check(gag.IsChoked(center, listener, 25), "Retail gag includes its center");
            Check(!gag.IsChoked(new Vector3(1, 0, 0), listener, 25), "Retail gag excludes exact radius boundary");
            Check(gag.IsChoked(new Vector3(.6f, 0, 0), listener, 25), "Retail gag preserves perpendicular sounds within radius");
            Check(!gag.IsChoked(new Vector3(0, 0, -.5f), listener, 25) && gag.IsChoked(new Vector3(0, 0, .5f), listener, 25), "Listener-facing overlap preserves the nearer sound");
            Check(gag.IsChoked(new Vector3(0, 0, -.5f), new Vector3(0, 0, -.5f), 25), "Close-listener branch preserves its authored radius");
            Check(gag.IsChoked(new Vector3(1.9f, 0, 0), listener, 5012.5f) && !gag.IsChoked(new Vector3(2, 0, 0), listener, 5012.5f), "Radius interpolates linearly across squared listener distance");
            Check(gag.IsChoked(new Vector3(2.9f, 0, 0), listener, 10000) && !gag.IsChoked(new Vector3(3, 0, 0), listener, 10000), "Maximum radius and strict boundary preserved");
            AudioBindings.OwnedGags.Remove(gag);
            Check(gag.IsChoked(new Vector3(.6f, 0, 0), listener, 25) == nativeControlBefore, "Unowned gag resumes the unchanged target implementation");
            var timeBefore = AudioSettings.dspTime;
            Check(binding.TryLimit(banks[1], AudioBindings.Policies[banks[1]], center, listener, 10), "Native limiter accepts the first owned sound");
            var recent = binding.Marked[^1];
            Check(recent.EndTime >= timeBefore + .15 && recent.EndTime <= AudioSettings.dspTime + .151 && recent.Radius == new Vector2(1, 3), "Native DSP expiry uses clip length times the source percent");
            Check(!binding.TryLimit(banks[1], AudioBindings.Policies[banks[1]], center, listener, 10), "Native limiter suppresses an overlapping owned sound");
            recent.EndTime = -1;
            Check(binding.TryLimit(banks[1], AudioBindings.Policies[banks[1]], center, listener, 10), "Native pool retires expired gags and permits replay");
            foreach (var item in binding.Marked) item.EndTime = -1;
            Check(!binding.TryLimit(banks[1], AudioBindings.Policies[banks[1]], center, listener, 130), "Retail rolloff excludes the exact outer boundary");
            var audio = MonoBehaviourSingleton<BetterAudio>.Instance;
            if (audio != null)
            {
            var cameraManager = CameraManager.Instance;
            var oldListener = audio.ListenerTransform;
            var oldCamera = cameraManager.Camera;
            var fixtureCamera = roots[0].AddComponent<Camera>();
            fixtureCamera.enabled = false;
            var cameraField = AccessTools.Field(typeof(CameraManager), "_camera");
            var listenerField = AccessTools.Field(typeof(BetterAudio), "_listenerTransform");
            try
            {
            // Playback is synchronous. Restore both menu bindings before yielding a frame.
            cameraField.SetValue(cameraManager, fixtureCamera);
            listenerField.SetValue(audio, fixtureCamera.transform);
            var position = fixtureCamera.transform.position + new Vector3(0, 0, 2);
            var effect = new Effect { Sound = banks[0], MaterialTypes = Array.Empty<EFT.Ballistics.MaterialType>() };
            var dispatchCount = AudioBindings.DispatchCount;
            effect.Emit(position, Vector3.up, null!, false, .001f, false, true, false, EPointOfView.ThirdPerson);
            var first = AudioBindings.LastSource;
            if (first != null) playing.Add(first);
            Check(AudioBindings.DispatchCount == dispatchCount + 1 && first != null && first.GetClip(0) != null, "Actual target Effect.Emit reaches owned native audio playback");
            Check(binding.Marked.AsValueEnumerable().All(g => !g.IsActive), "Visible effect bypasses gag creation when requested");
            effect.Emit(position, Vector3.up, null!, false, .001f, false, false, false, EPointOfView.ThirdPerson);
            var second = AudioBindings.LastSource;
            if (second != null) playing.Add(second);
            Check(second && binding.Marked.AsValueEnumerable().Any(g => g.IsActive), "Hidden effect creates an owned native gag and plays");
            effect.Emit(position, Vector3.up, null!, false, .001f, false, false, false, EPointOfView.ThirdPerson);
            Check(AudioBindings.LastSource == null, "Actual effect playback suppresses an immediate overlapping repeat");
            foreach (var source in playing) source.Release();
            playing.Clear();
            }
            finally
            {
                cameraField.SetValue(cameraManager, oldCamera);
                listenerField.SetValue(audio, oldListener);
                UnityEngine.Object.Destroy(fixtureCamera);
            }
            Check(cameraManager.Camera == oldCamera && audio.ListenerTransform == oldListener, "Native menu camera and audio listener bindings restored before yielding");
            playbackTested = true;
            }
            roots[0].SetActive(false);
            Check(!binding.Loaded && binding.Clips.Count == 0, "Natural disable releases the preloader's clip claims");
            roots[0].SetActive(true);
            Check(binding.Loaded && binding.Clips.Count == 3 && AudioBindings.Catalogs.Count == counts.Item1 + 1, "Natural re-enable reloads without duplicate policy registrations");
            gags.AddRange(binding.Marked);
        }
        finally
        {
            foreach (var source in playing) if (source) source.Release();
            foreach (var gag in gags) if (binding == null || !binding.Marked.Contains(gag)) AudioBindings.OwnedGags.Remove(gag);
            if (scene.IsValid() && scene.isLoaded)
            {
                var unload = SceneManager.UnloadSceneAsync(scene);
                if (unload != null) await UniTask.WaitUntil(() => unload.isDone);
            }
            if (bundle != null) bundle.Unload(true);
            Application.logMessageReceived -= Error;
            Check(AudioBindings.Catalogs.Count == counts.Item1 && AudioBindings.Policies.Count == counts.Item2 && AudioBindings.Blends.Count == counts.Item3, "All audio policies removed after scene unload");
            Check(gags.AsValueEnumerable().All(g => !AudioBindings.OwnedGags.TryGetValue(g, out _)), "All native gag ownership released");
            Check(SceneManager.sceneCount == scenes, "Original menu scene count restored");
            Check(errors.Count == 0, "No Unity errors: " + string.Join("; ", errors));
        }
        return new { Passed = true, BundleSha256 = input.Sha256, CheckCount = checks.Count, Checks = checks, UnityErrors = errors, ClipPcmSha256 = clipHashes, PlaybackTested = playbackTested,
            Scope = "Native serialized clips/banks, distance selection, scoped fade, native DSP/pool expiry with retail gag geometry and preloader lifecycle. Effect.Emit playback is tested only when the native gameplay audio system exists; full-raid playback/acoustics remain required." };
    }
}
