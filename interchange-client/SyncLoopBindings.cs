using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using EFT;
using HarmonyLib;
using Manimal.Interchange.Components;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.Audio;
using NativeLoop = Audio.AudioCulling.SyncLoopSoundPlayer;

namespace Manimal.Interchange.Client;

internal static class DspLoopSchedule
{
    internal static int Plan(ref double grid, double dsp, double ahead, double delay, double period, int catchUp, out double first)
    {
        var target = dsp + ahead + delay;
        var delta = target - grid;
        if (double.IsNaN(delta) || double.IsInfinity(delta) || Math.Abs(delta) > catchUp * period)
        {
            grid = target; delta = 0;
        }
        first = grid - delay;
        if (delta < 0) return 0;
        // Pinned native helper 0x18061a2f8 rounds downward, including fractions.
        var count = Math.Min((int)Math.Floor(delta / period) + 1, Math.Max(1, catchUp));
        grid += count * period;
        return count;
    }
}

internal static class SyncLoopBindings
{
    private static readonly FieldInfo Sources = AccessTools.Field(typeof(NativeLoop), "_sources");
    private static readonly FieldInfo Clip = AccessTools.Field(typeof(NativeLoop), "_clip");
    private static readonly FieldInfo Mixer = AccessTools.Field(typeof(NativeLoop), "_outputMixerGroup");
    private static readonly FieldInfo Delay = AccessTools.Field(typeof(NativeLoop), "_useSoundDelayCalculation");
    private static readonly FieldInfo Playing = AccessTools.Field(typeof(NativeLoop), "_isPlaying");
    private static readonly Dictionary<NativeLoop, Binding> Bindings = new();
    internal static int Count => Bindings.Count;
    internal static void Enable() { SyncLoopPolicy.Destroyed += Remove; SyncLoopPolicy.StateRequested += ApplyState; }
    internal static void Disable()
    {
        SyncLoopPolicy.Destroyed -= Remove; SyncLoopPolicy.StateRequested -= ApplyState;
        foreach (var binding in Bindings.Values) binding.Stop();
        Bindings.Clear();
    }
    internal static bool TryGet(NativeLoop owner, out Binding binding)
    {
        if (Bindings.TryGetValue(owner, out binding)) return true;
        if (!owner.TryGetComponent<SyncLoopPolicy>(out var policy) || policy.Owner != owner) return false;
        binding = new Binding(owner, policy); Bindings.Add(owner, binding); return true;
    }
    private static void ApplyState(SyncLoopPolicy policy, bool state)
    {
        if (policy.Owner is not NativeLoop owner || !TryGet(owner, out var binding) || binding.Policy != policy) return;
        if (state) binding.Play(); else binding.Stop();
    }
    private static void Remove(SyncLoopPolicy policy)
    {
        if (policy.Owner is not NativeLoop owner || !Bindings.TryGetValue(owner, out var binding) || binding.Policy != policy) return;
        Bindings.Remove(owner); binding.Stop();
    }

    internal sealed class Binding
    {
        private readonly NativeLoop _owner;
        internal SyncLoopPolicy Policy { get; }
        private AudioSource[] _sources = Array.Empty<AudioSource>();
        private double[] _grid = Array.Empty<double>();
        private double _period;
        private bool _delay, _initialized, _playing;
        private CancellationTokenSource? _cancellation;
        internal Func<AudioSource, double>? ProbeDelay;
        internal Action<int, double, double>? Scheduled;
        internal Binding(NativeLoop owner, SyncLoopPolicy policy) { _owner = owner; Policy = policy; }
        internal bool IsPlaying => _playing;
        private static bool Usable(AudioSource source) => source && source.enabled && source.gameObject.activeInHierarchy;
        private double AdditionalDelay(AudioSource source) => !_delay || !source ? 0 :
            ProbeDelay != null ? ProbeDelay(source) : AudioUtils.CalculatePhysicSoundDelay(source.transform.position);

        internal void Initialize()
        {
            if (_initialized) return;
            var clip = (AudioClip)Clip.GetValue(_owner);
            if (!clip || clip.frequency <= 0 || clip.samples <= 0) throw new InvalidDataException("Invalid synchronized alarm clip: " + Policy.SourceKey);
            _period = (double)clip.samples / clip.frequency;
            _sources = (AudioSource[])Sources.GetValue(_owner);
            if (_sources == null || _sources.Length == 0)
            {
                _sources = _owner.GetComponentsInChildren<AudioSource>(true); Sources.SetValue(_owner, _sources);
            }
            _delay = (bool)Delay.GetValue(_owner);
            var mixer = (AudioMixerGroup)Mixer.GetValue(_owner);
            foreach (var source in _sources)
            {
                if (!source) continue;
                source.outputAudioMixerGroup = mixer; source.playOnAwake = false; source.loop = false; source.clip = clip;
            }
            _initialized = true;
        }
        internal void Play()
        {
            if (_playing) return;
            Stop();
            if (!_owner || !_owner.gameObject.activeInHierarchy) return;
            Initialize();
            var common = AudioSettings.dspTime + Policy.ScheduleAheadTime;
            var maxDelay = 0d;
            foreach (var source in _sources) if (Usable(source)) maxDelay = Math.Max(maxDelay, AdditionalDelay(source));
            common += maxDelay;
            _grid = new double[_sources.Length];
            for (var i = 0; i < _grid.Length; i++) _grid[i] = common;
            _playing = true; Playing.SetValue(_owner, true);
            var cancellation = _cancellation = new CancellationTokenSource();
            Run(cancellation).Forget();
            if (_playing) Policy.NotifyState(true);
        }
        internal void Stop()
        {
            var cancellation = _cancellation; _cancellation = null;
            cancellation?.Cancel();
            _playing = false; if (_owner) Playing.SetValue(_owner, false);
            // Retail stops future scheduling. Already scheduled audio finishes
            // naturally; disabling/destroying AudioSources follows Unity's lifecycle.
            if (Policy) Policy.NotifyState(false);
        }
        private async UniTaskVoid Run(CancellationTokenSource cancellation)
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    Tick(AudioSettings.dspTime);
                    await UniTask.Yield(PlayerLoopTiming.Update, cancellation.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Plugin.Log.LogError(error); if (ReferenceEquals(_cancellation, cancellation)) Stop(); }
            finally { cancellation.Dispose(); }
        }
        internal void Tick(double dsp)
        {
            for (var i = 0; i < _sources.Length; i++)
            {
                var source = _sources[i]; if (!Usable(source)) continue;
                var count = DspLoopSchedule.Plan(ref _grid[i], dsp, Policy.ScheduleAheadTime, AdditionalDelay(source), _period, Policy.MaxCatchUpPerFrame, out var first);
                for (var play = 0; play < count; play++)
                {
                    var start = first + play * _period;
                    source.PlayScheduled(Math.Max(0, start)); source.SetScheduledEndTime(start + _period);
                    Scheduled?.Invoke(i, start, start + _period);
                }
            }
        }
    }
}

internal sealed class SyncLoopInitPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(NativeLoop), "method_0");
    [PatchPrefix]
    private static bool Prefix(NativeLoop __instance)
    { if (!SyncLoopBindings.TryGet(__instance, out var binding)) return true; binding.Initialize(); return false; }
}
internal sealed class SyncLoopPlayPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(NativeLoop), "Play");
    [PatchPrefix]
    private static bool Prefix(NativeLoop __instance)
    { if (!SyncLoopBindings.TryGet(__instance, out var binding)) return true; binding.Play(); return false; }
}
internal sealed class SyncLoopStopPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(NativeLoop), "Stop");
    [PatchPrefix]
    private static bool Prefix(NativeLoop __instance)
    { if (!SyncLoopBindings.TryGet(__instance, out var binding)) return true; binding.Stop(); return false; }
}
