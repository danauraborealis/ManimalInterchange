using System;
using System.Collections.Generic;
using System.Reflection;
using Audio.Effects;
using EFT.GameTriggers;
using HarmonyLib;
using Manimal.Interchange.Components;
using SPT.Reflection.Patching;
using UnityEngine;
using ZLinq;

namespace Manimal.Interchange.Client;

internal static class AdvancedSoundBindings
{
    internal static readonly FieldInfo SourceField = AccessTools.Field(typeof(HandlerPlaySoundAdvanced), "_betterSource");
    private static readonly FieldInfo FiltersField = AccessTools.Field(typeof(BetterSource), "_audioFilters");
    private static readonly FieldInfo HighPassField = AccessTools.Field(typeof(AudioEQFilterBase), "_highPassEnabled");
    private static readonly Dictionary<AdvancedSoundPolicy, Lease> Leases = new();
    internal static int Count => Leases.Count;
    private sealed class Lease
    {
        internal AdvancedSoundPolicy Policy = null!;
        internal BetterSource Source = null!;
        internal (AudioSource Source, AnimationCurve Curve, AudioRolloffMode Mode)[] Curves = Array.Empty<(AudioSource, AnimationCurve, AudioRolloffMode)>();
        internal (IAudioEQFilter Filter, bool Enabled)[] Filters = Array.Empty<(IAudioEQFilter, bool)>();
        internal void Released(BetterSource source)
        {
            if (source != Source) return;
            source.OnReleased -= Released;
            foreach (var state in Curves)
                if (state.Source) { state.Source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, state.Curve); state.Source.rolloffMode = state.Mode; }
            foreach (var state in Filters) state.Filter.SetActiveHighPass(state.Enabled);
            if (ReferenceEquals(SourceField.GetValue(Policy.Handler), Source)) SourceField.SetValue(Policy.Handler, null);
            Leases.Remove(Policy);
        }
    }
    internal static void Enable() => AdvancedSoundPolicy.Destroyed += Remove;
    internal static void Disable()
    {
        AdvancedSoundPolicy.Destroyed -= Remove;
        foreach (var policy in Leases.Keys.AsValueEnumerable().ToArray()) Remove(policy);
    }
    private static void Remove(AdvancedSoundPolicy policy)
    {
        if (!Leases.TryGetValue(policy, out var lease)) return;
        lease.Source.Release();
    }
    internal static AdvancedSoundPolicy? Policy(HandlerPlaySoundAdvanced handler) =>
        handler.TryGetComponent<AdvancedSoundPolicy>(out var policy) && policy.Handler == handler ? policy : null;
    internal static void Before(HandlerPlaySoundAdvanced handler, HandlerPlaySoundAdvanced.PlaySoundConfig config)
    {
        var policy = Policy(handler);
        if (policy != null && config.AudioClip) Remove(policy);
    }
    internal static void After(HandlerPlaySoundAdvanced handler)
    {
        var policy = Policy(handler);
        var source = SourceField.GetValue(handler) as BetterSource;
        if (policy == null || source == null || Leases.ContainsKey(policy)) return;
        // Each pooled source can own multiple playback/reverb channels.
        var channels = source.GetComponentsInChildren<AudioSource>(true);
        var filters = FiltersField.GetValue(source) as IAudioEQFilter[] ?? Array.Empty<IAudioEQFilter>();
        if (filters.AsValueEnumerable().Any(f => f is not AudioEQFilterBase)) throw new InvalidOperationException("Unreviewed target audio filter type");
        var lease = new Lease { Policy = policy, Source = source,
            Curves = policy.RolloffCurve != null && policy.RolloffCurve.length > 0
                ? channels.AsValueEnumerable().Select(s => (s, s.GetCustomCurve(AudioSourceCurveType.CustomRolloff), s.rolloffMode)).ToArray()
                : Array.Empty<(AudioSource, AnimationCurve, AudioRolloffMode)>(),
            Filters = filters.AsValueEnumerable().Select(f => (f, (bool)HighPassField.GetValue(f))).ToArray() };
        Leases.Add(policy, lease);
        source.OnReleased += lease.Released;
        foreach (var state in lease.Curves) { state.Source.rolloffMode = AudioRolloffMode.Custom; state.Source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, policy.RolloffCurve); }
        source.EnabledHighPassFilter(policy.EnableHighPassFilter);
    }
}

internal sealed class AdvancedSoundPolicyPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(HandlerPlaySoundAdvanced), "PlaySound", new[] { typeof(HandlerPlaySoundAdvanced.PlaySoundConfig) });
    [PatchPrefix]
    private static void Prefix(HandlerPlaySoundAdvanced __instance, HandlerPlaySoundAdvanced.PlaySoundConfig __0) => AdvancedSoundBindings.Before(__instance, __0);
    [PatchPostfix]
    private static void Postfix(HandlerPlaySoundAdvanced __instance) => AdvancedSoundBindings.After(__instance);
}
