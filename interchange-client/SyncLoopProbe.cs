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
using UnityEngine.SceneManagement;
using ZLinq;
using NativeLoop = Audio.AudioCulling.SyncLoopSoundPlayer;

namespace Manimal.Interchange.Client;

internal static class SyncLoopProbe
{
    private sealed class Input { public string Bundle = ""; public string Sha256 = ""; public string Scene = ""; }
    private sealed class Scheduled { public int source; public double start, end, delay; }
    internal static async UniTask<object> Run(CancellationToken token)
    {
        var input = JsonConvert.DeserializeObject<Input>(File.ReadAllText(Path.Combine(Plugin.Root, "sync-loop-validation.json")))!;
        var path = ManifestRules.Resolve(Plugin.Root, input.Bundle);
        if (ManifestRules.Hash(path) != input.Sha256) throw new InvalidDataException("Alarm fixture changed");
        var before = SceneManager.sceneCount; var bindingCount = SyncLoopBindings.Count;
        var checks = new List<string>(); var errors = new List<string>(); var scheduled = new List<Scheduled>(); var states = new List<bool>();
        void Check(bool pass, string name) { if (!pass) throw new InvalidDataException(name); checks.Add(name); }
        void Error(string message, string stack, LogType kind) { if (kind is LogType.Error or LogType.Exception or LogType.Assert) errors.Add(message + "\n" + stack); }
        void Plan(double grid, double dsp, double delay, double period, int cap, int expected, double next, string name)
        {
            var count = DspLoopSchedule.Plan(ref grid, dsp, .1, delay, period, cap, out _);
            Check(count == expected && Math.Abs(grid - next) < 1e-8, name);
        }
        Plan(10, 9.8, 0, .25, 4, 0, 10, "No scheduling before the shared arrival grid");
        Plan(10, 9.9, 0, .25, 4, 1, 10.25, "Exact boundary schedules one loop");
        Plan(10, 10.03, 0, .25, 4, 1, 10.25, "Fractional interval uses floor instead of ceiling");
        Plan(10, 10.4, 0, .25, 4, 3, 10.75, "Missed frames catch up by whole periods");
        Plan(10, 10.9, 0, .25, 4, 4, 11, "Catch-up count is bounded at four");
        Plan(10, 20, .02, .25, 4, 1, 20.37, "Long discontinuity resets to current DSP plus delay");
        Plan(20, 10, 0, .25, 4, 1, 10.35, "Backward discontinuity resets without an unbounded stall");
        Plan(double.NaN, 10, 0, .25, 4, 1, 10.35, "Invalid grid is reset");
        AssetBundle? bundle = null; Scene scene = default;
        Application.logMessageReceived += Error;
        try
        {
            token.ThrowIfCancellationRequested();
            var request = AssetBundle.LoadFromFileAsync(path); await UniTask.WaitUntil(() => request.isDone);
            bundle = request.assetBundle; Check(bundle, "Bundle loaded");
            Check(bundle!.GetAllScenePaths().AsValueEnumerable().SequenceEqual(new[] { input.Scene }), "Exact fixture scene");
            var load = SceneManager.LoadSceneAsync(input.Scene, LoadSceneMode.Additive); await UniTask.WaitUntil(() => load.isDone);
            scene = SceneManager.GetSceneByPath(input.Scene); var root = scene.GetRootGameObjects().AsValueEnumerable().Single();
            var policy = root.GetComponentInChildren<SyncLoopPolicy>(true); var native = (NativeLoop)policy.Owner;
            var control = root.transform.Find("Control").GetComponent<NativeLoop>();
            Check(!root.activeSelf && policy.ScheduleAheadTime == .1f && policy.MaxCatchUpPerFrame == 4, "Serialized owned scheduling values");
            Check(SyncLoopBindings.TryGet(native, out var binding) && !SyncLoopBindings.TryGet(control, out _), "Adapter scopes only to the serialized native owner");
            binding.ProbeDelay = source => source.transform.localPosition.x * .005;
            binding.Scheduled = (index, start, end) => scheduled.Add(new Scheduled { source = index, start = start, end = end, delay = index * .005 });
            policy.StateChanged += states.Add;
            root.SetActive(true); await UniTask.Yield();
            var sources = native.GetComponentsInChildren<AudioSource>();
            var clip = (AudioClip)AccessTools.Field(typeof(NativeLoop), "_clip").GetValue(native);
            var period = (double)clip.samples / clip.frequency;
            Check(sources.Length == 6 && sources.AsValueEnumerable().All(s => s.clip == clip && !s.loop && !s.playOnAwake), "Native Awake configures all six sources without automatic playback");
            var samples = new float[clip.samples * clip.channels];
            Check(clip.GetData(samples, 0) && samples.AsValueEnumerable().Any(value => value != 0), "Real PCM is available to the target audio engine");
            native.Play(); native.Play();
            Check(binding.IsPlaying && states.AsValueEnumerable().SequenceEqual(new[] { false, true }), "Native Play is idempotent and emits the retail state sequence");
            await UniTask.Delay(170, ignoreTimeScale: true, cancellationToken: token);
            Check(scheduled.AsValueEnumerable().Select(s => s.source).Distinct().Count() == 6, "All six native sources received DSP schedules");
            var arrival = scheduled[0].start + scheduled[0].delay;
            Check(scheduled.AsValueEnumerable().All(s => Math.Abs((s.start + s.delay - arrival) / period - Math.Round((s.start + s.delay - arrival) / period)) < 1e-7), "Propagation-adjusted speaker arrivals share one sample-derived grid");
            Check(scheduled.AsValueEnumerable().All(s => Math.Abs(s.end - s.start - period) < 1e-7), "Scheduled end times preserve exact samples/frequency duration");
            Check(sources.AsValueEnumerable().Any(s => s.isPlaying), "Unity audio engine entered scheduled playback");
            native.Stop(); var stoppedCount = scheduled.Count;
            Check(!binding.IsPlaying && sources.AsValueEnumerable().Any(s => s.isPlaying) && !states[states.Count - 1], "Stop cancels future scheduling and retains the currently playing tail");
            await UniTask.Delay(500, ignoreTimeScale: true, cancellationToken: token);
            Check(scheduled.Count == stoppedCount && sources.AsValueEnumerable().All(s => !s.isPlaying), "Stopped scheduler stays cancelled while existing clips finish");
            sources[5].enabled = false; policy.ApplyState(true); var disabledStart = scheduled.Count;
            await UniTask.Delay(180, ignoreTimeScale: true, cancellationToken: token);
            Check(scheduled.AsValueEnumerable().Skip(disabledStart).All(s => s.source != 5), "Disabled speaker receives no schedules");
            sources[5].enabled = true; await UniTask.Delay(300, ignoreTimeScale: true, cancellationToken: token);
            Check(scheduled.AsValueEnumerable().Skip(disabledStart).Any(s => s.source == 5), "Reenabled speaker rejoins the shared loop grid");
            policy.ApplyState(false); Check(!binding.IsPlaying, "Owned state dispatch stops scheduling");
            var auto = AccessTools.Field(typeof(NativeLoop), "_playOnAwake"); auto.SetValue(native, true);
            root.SetActive(false); root.SetActive(true); await UniTask.Yield();
            Check(binding.IsPlaying, "Native OnEnable restarts the owned automatic alarm");
            root.SetActive(false); var disabledCount = scheduled.Count;
            await UniTask.Delay(100, ignoreTimeScale: true, cancellationToken: token);
            Check(!binding.IsPlaying && scheduled.Count == disabledCount, "Native OnDisable cancels the active generation");
            auto.SetValue(native, false); root.SetActive(true); control.Play();
            Check((bool)AccessTools.Field(typeof(NativeLoop), "_isPlaying").GetValue(control) && !SyncLoopBindings.TryGet(control, out _), "Unowned native Play remains functional");
            control.Stop(); Check(!(bool)AccessTools.Field(typeof(NativeLoop), "_isPlaying").GetValue(control), "Unowned native Stop remains functional");
            policy.ApplyState(true); token.ThrowIfCancellationRequested();
        }
        catch (Exception error) { throw new InvalidDataException("Alarm probe stopped after " + checks[checks.Count - 1], error); }
        finally
        {
            if (scene.IsValid() && scene.isLoaded) { var unload = SceneManager.UnloadSceneAsync(scene); await UniTask.WaitUntil(() => unload.isDone); }
            if (bundle) bundle!.Unload(true);
            await UniTask.Yield(); Application.logMessageReceived -= Error;
        }
        Check(SceneManager.sceneCount == before && SyncLoopBindings.Count == bindingCount, "Scene unload cancels scheduling and removes owned bindings");
        Check(errors.Count == 0, "No Unity errors in alarm playback or cleanup" + (errors.Count == 0 ? "" : ": " + string.Join("\n", errors)));
        return new { passed = true, checks, errors, scheduled, states, bundle = input.Sha256,
            scope = "Native serialized six-speaker PCM scheduling, controlled propagation delays, floor/catch-up/reset math, stop tails, lifecycle and unowned controls. Actual raid listener propagation and map alarm switching require full-map validation." };
    }
}
