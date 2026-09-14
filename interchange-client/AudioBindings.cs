using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using EFT;
using EFT.EnvironmentEffect;
using HarmonyLib;
using Manimal.Interchange.Components;
using SPT.Reflection.Patching;
using UnityEngine;
using ZLinq;
using Effect = Systems.Effects.Effects.Effect;

namespace Manimal.Interchange.Client;

internal static class AudioBindings
{
    internal sealed class BankPolicy
    {
        internal Binding Owner = null!;
        internal bool LimitVisible;
        internal Vector2 Radius;
        internal float Percent;
        internal bool ShouldLimit(bool allow, bool visible, bool melee) => melee || (allow && (LimitVisible || !visible));
    }

    internal sealed class Binding
    {
        internal readonly AudioCatalog Catalog;
        internal readonly GagSoundLimiter Limiter = new();
        internal readonly List<Gag> Marked = new();
        internal readonly List<AudioClip> Clips = new();
        internal bool Loaded;
        internal Binding(AudioCatalog catalog) { Catalog = catalog; Limiter.PrecacheGag("Impact"); }
        internal bool TryLimit(SoundBank bank, BankPolicy policy, Vector3 position, Vector3 listener, float distance)
        {
            var squared = distance * distance;
            if (!(squared < bank.Rolloff * bank.Rolloff)) return false;
            if (!Limiter.CheckGagsLimit(position, listener, squared, "Impact")) return false;
            Limiter.InitGag(position, bank.ClipLength, Mathf.Max(bank.Rolloff, 5), policy.Percent, policy.Radius, "Impact");
            var gag = ((Dictionary<string, List<Gag>>)GagsField.GetValue(Limiter))["Impact"][^1];
            if (!OwnedGags.TryGetValue(gag, out _)) { OwnedGags.Add(gag, this); Marked.Add(gag); }
            return true;
        }
        internal void Load()
        {
            if (Loaded) return;
            foreach (var item in Catalog.Banks) if (item is SoundBank bank)
                foreach (var environment in bank.Environments)
                    foreach (var variety in environment.Clips)
                        foreach (var clip in variety.Clips)
                        {
                            if (!clip || Clips.Contains(clip)) continue;
                            AcquireClip(clip);
                            Clips.Add(clip);
                        }
            Loaded = true;
        }
        internal void Unload()
        {
            if (!Loaded) return;
            foreach (var clip in Clips) ReleaseClip(clip);
            Clips.Clear();
            Loaded = false;
        }
        internal void Clear()
        {
            Unload();
            foreach (var gag in Marked) OwnedGags.Remove(gag);
            Marked.Clear();
            ((Dictionary<string, List<Gag>>)GagsField.GetValue(Limiter)).Clear();
        }
    }

    private static readonly FieldInfo GagsField = AccessTools.Field(typeof(GagSoundLimiter), "Gags");
    private static readonly Dictionary<AudioClip, (int Count, bool RestoreUnloaded)> ClipReferences = new();
    private static void AcquireClip(AudioClip clip)
    {
        if (ClipReferences.TryGetValue(clip, out var state)) { ClipReferences[clip] = (state.Count + 1, state.RestoreUnloaded); return; }
        var restore = clip.loadState == AudioDataLoadState.Unloaded;
        if (!clip.LoadAudioData()) throw new InvalidOperationException("Audio data failed to load: " + clip.name);
        ClipReferences.Add(clip, (1, restore));
    }
    private static void ReleaseClip(AudioClip clip)
    {
        var state = ClipReferences[clip];
        if (state.Count > 1) { ClipReferences[clip] = (state.Count - 1, state.RestoreUnloaded); return; }
        if (state.RestoreUnloaded && clip) clip.UnloadAudioData();
        ClipReferences.Remove(clip);
    }
    internal static readonly ConditionalWeakTable<Gag, Binding> OwnedGags = new();
    internal static readonly Dictionary<AudioCatalog, Binding> Catalogs = new();
    internal static readonly Dictionary<SoundBank, BankPolicy> Policies = new();
    internal static readonly Dictionary<DistanceBlendOptions, (AudioCatalog Owner, int Fade)> Blends = new();
    internal static int DispatchCount;
    internal static BetterSource? LastSource;
    private static readonly Action<Effect, Vector3, float, float, bool, bool> Original =
        (Action<Effect, Vector3, float, float, bool, bool>)AccessTools.Method(typeof(Effect), "PlaySound").CreateDelegate(typeof(Action<Effect, Vector3, float, float, bool, bool>));

    internal static void Enable()
    {
        AudioCatalog.Created += Register;
        AudioCatalog.Enabled += OnEnable;
        AudioCatalog.Disabled += OnDisable;
        AudioCatalog.Destroyed += Remove;
    }
    internal static void Disable()
    {
        AudioCatalog.Created -= Register;
        AudioCatalog.Enabled -= OnEnable;
        AudioCatalog.Disabled -= OnDisable;
        AudioCatalog.Destroyed -= Remove;
        foreach (var owner in Catalogs.Keys.AsValueEnumerable().ToArray()) Remove(owner);
    }
    private static void Register(AudioCatalog catalog)
    {
        if (catalog.LimitedBanks.Length != catalog.LimitIfVisible.Length || catalog.LimitedBanks.Length != catalog.LimitRadius.Length ||
            catalog.LimitedBanks.Length != catalog.LimitPercentOfClip.Length || catalog.BlendProfiles.Length != catalog.FadeTypes.Length)
            throw new InvalidOperationException("Audio catalog arrays differ");
        if (Catalogs.ContainsKey(catalog)) return;
        var binding = new Binding(catalog);
        Catalogs.Add(catalog, binding);
        for (var i = 0; i < catalog.LimitedBanks.Length; i++)
        {
            var bank = catalog.LimitedBanks[i] as SoundBank ?? throw new InvalidOperationException("Audio limiter bank binding missing");
            Policies.Add(bank, new BankPolicy { Owner = binding, LimitVisible = catalog.LimitIfVisible[i], Radius = catalog.LimitRadius[i], Percent = catalog.LimitPercentOfClip[i] });
        }
        for (var i = 0; i < catalog.BlendProfiles.Length; i++)
        {
            var blend = catalog.BlendProfiles[i] as DistanceBlendOptions ?? throw new InvalidOperationException("Audio blend binding missing");
            if (catalog.FadeTypes[i] != 3) throw new InvalidOperationException("Unreviewed audio fade");
            Blends.Add(blend, (catalog, catalog.FadeTypes[i]));
        }
    }
    private static void OnEnable(AudioCatalog catalog) { if (catalog.LoadOnEnable) Catalogs[catalog].Load(); }
    private static void OnDisable(AudioCatalog catalog) { if (catalog.UnloadOnDisable && Catalogs.TryGetValue(catalog, out var binding)) binding.Unload(); }
    private static void Remove(AudioCatalog catalog)
    {
        if (!Catalogs.TryGetValue(catalog, out var binding)) return;
        binding.Clear();
        foreach (var item in catalog.LimitedBanks) if (item is SoundBank bank && Policies.TryGetValue(bank, out var policy) && policy.Owner == binding) Policies.Remove(bank);
        foreach (var item in catalog.BlendProfiles) if (item is DistanceBlendOptions blend && Blends.TryGetValue(blend, out var policy) && policy.Owner == catalog) Blends.Remove(blend);
        Catalogs.Remove(catalog);
    }
    internal static void Dispatch(Effect effect, Vector3 position, float distance, float volume, bool firstPerson, bool melee, bool visible)
    {
        var bank = effect.Sound;
        if (!bank || !Policies.TryGetValue(bank, out var policy) || bank.Physical || (firstPerson && effect.SoundFP))
        { Original(effect, position, distance, volume, firstPerson, melee); return; }
        DispatchCount++;
        LastSource = null;
        if (volume <= 0) return;
        var audio = MonoBehaviourSingleton<BetterAudio>.Instance;
        if (policy.ShouldLimit(bank.AllowLimitedPlay, visible, melee) && !policy.Owner.TryLimit(bank, policy, position, audio.ListenerTransform.position, distance)) return;
        var environment = bank.HasEnvironment && EnvironmentManager.Instance ? EnvironmentManager.Instance.GetEnvironmentByPos(position) : EnvironmentType.Outdoor;
        LastSource = audio.PlayAtPoint(position, bank, distance, volume, -1, environment, EOcclusionTest.OneShotPropagation, false, false);
    }
}

internal sealed class EffectSoundPolicyPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Effect), "Emit");
    [PatchTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source, MethodBase original)
    {
        var target = AccessTools.Method(typeof(Effect), "PlaySound");
        var replacement = AccessTools.Method(typeof(AudioBindings), nameof(AudioBindings.Dispatch));
        var parameters = original.GetParameters();
        var visibleIndex = parameters.AsValueEnumerable().First(p => p.Name == "isHitPointVisible").Position + 1;
        var code = source.AsValueEnumerable().ToList();
        var matches = 0;
        for (var i = 0; i < code.Count; i++)
        {
            if (!code[i].Calls(target)) continue;
            var load = new CodeInstruction(OpCodes.Ldarg, visibleIndex);
            load.MoveLabelsFrom(code[i]);
            load.MoveBlocksFrom(code[i]);
            code.Insert(i++, load);
            code[i].opcode = OpCodes.Call;
            code[i].operand = replacement;
            matches++;
        }
        if (matches != 1) throw new InvalidOperationException("Target effect playback call changed");
        return code;
    }
}

internal sealed class DistanceAudioFadePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(SoundBank), "PickClipsByDistance");
    [PatchPostfix]
    private static void Postfix(SoundBank __instance, ref float __2)
    {
        if (__instance.BlendOptions && AudioBindings.Blends.ContainsKey(__instance.BlendOptions)) __2 = AudioCatalog.EaseInOut(__2);
    }
}

internal sealed class OwnedSoundGagPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Gag), "IsChoked");
    [PatchPrefix]
    private static bool Prefix(Gag __instance, Vector3 __0, Vector3 __1, float __2, ref bool __result)
    {
        if (!AudioBindings.OwnedGags.TryGetValue(__instance, out _)) return true;
        __result = AudioCatalog.IsChoked(__instance.Position, __instance.Radius, __instance.Rolloff, __0, __1, __2);
        return false;
    }
}
