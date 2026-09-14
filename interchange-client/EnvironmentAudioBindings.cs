using System;
using System.Collections.Generic;
using System.Reflection;
using Audio.SpatialSystem;
using EFT.GlobalEvents;
using EFT.GlobalEvents.AudioEvents;
using HarmonyLib;
using Manimal.Interchange.Components;
using UnityEngine;
using UnityEngine.Audio;

namespace Manimal.Interchange.Client;

/// <summary>
/// Recreates the source HandlerEnvironmentAudioSource initialization against
/// the target audio/spatial services.  The source handler is absent from the
/// target assembly; EnvironmentAudioSource is projected at the same
/// component path ID and retains the authored mixer object references.
/// </summary>
internal static class EnvironmentAudioBindings
{
    internal sealed class Binding
    {
        internal readonly EnvironmentAudioSource Marker;
        internal readonly AudioSource AudioSource;
        internal Action? UnsubscribeSpatialInitialized;
        internal BetterSource? BetterSource;
        internal int ContainerId = -1;
        internal bool Initialized;
        internal bool Disposed;

        internal Binding(EnvironmentAudioSource marker, AudioSource audioSource)
        {
            Marker = marker;
            AudioSource = audioSource;
        }
    }

    private const string RoomStorageFieldName = "_audioRoomStorage";
    private const string AmbientInMixerFieldName = "AmbientInMixer";
    private const string AmbientOutMixerFieldName = "AmbientOutMixer";
    private static readonly FieldInfo RoomStorageField = AccessTools.Field(typeof(SpatialAudioSystem), RoomStorageFieldName)
        ?? throw new MissingMemberException(typeof(SpatialAudioSystem).FullName, RoomStorageFieldName);
    private static readonly FieldInfo AmbientInMixerField = AccessTools.Field(typeof(BetterAudio), AmbientInMixerFieldName)
        ?? throw new MissingMemberException(typeof(BetterAudio).FullName, AmbientInMixerFieldName);
    private static readonly FieldInfo AmbientOutMixerField = AccessTools.Field(typeof(BetterAudio), AmbientOutMixerFieldName)
        ?? throw new MissingMemberException(typeof(BetterAudio).FullName, AmbientOutMixerFieldName);

    internal static readonly Dictionary<EnvironmentAudioSource, Binding> Bindings = new();
    internal static int Count => Bindings.Count;

    internal static void Enable()
    {
        EnvironmentAudioSource.Created += Register;
        EnvironmentAudioSource.Enabled += Retry;
        EnvironmentAudioSource.Destroyed += Remove;
    }

    internal static void Disable()
    {
        EnvironmentAudioSource.Created -= Register;
        EnvironmentAudioSource.Enabled -= Retry;
        EnvironmentAudioSource.Destroyed -= Remove;
        var markers = new List<EnvironmentAudioSource>(Bindings.Keys);
        foreach (var marker in markers) Remove(marker);
    }

    private static void Register(EnvironmentAudioSource marker)
    {
        if (Bindings.ContainsKey(marker)) return;
        var source = marker.GetComponent<AudioSource>();
        if (!source) throw new InvalidOperationException("Environment audio marker has no AudioSource: " + marker.name);
        var binding = new Binding(marker, source);
        Bindings.Add(marker, binding);
        TryInitialize(binding);
    }

    private static void Retry(EnvironmentAudioSource marker)
    {
        if (Bindings.TryGetValue(marker, out var binding)) TryInitialize(binding);
    }

    private static void Remove(EnvironmentAudioSource marker)
    {
        if (!Bindings.TryGetValue(marker, out var binding)) return;
        binding.Disposed = true;
        if (binding.UnsubscribeSpatialInitialized != null)
        {
            var unsubscribe = binding.UnsubscribeSpatialInitialized;
            binding.UnsubscribeSpatialInitialized = null;
            unsubscribe!();
        }
        if (binding.AudioSource) binding.AudioSource.Stop();
        var betterSource = binding.BetterSource;
        binding.BetterSource = null;
        if (betterSource)
        {
            betterSource!.Release();
        }
        Bindings.Remove(marker);
    }

    private static void WaitForSpatialInitialization(Binding binding)
    {
        if (binding.UnsubscribeSpatialInitialized != null || binding.Disposed) return;
        var events = GlobalEventsController.Instance;
        if (events is null) return;
        binding.UnsubscribeSpatialInitialized = events.SubscribeOnEvent<SpatialAudioSystemInitializedEvent>(
            _ => TryInitialize(binding));
    }

    private static void TryInitialize(Binding binding)
    {
        if (binding.Disposed || binding.Initialized || !binding.Marker || !binding.AudioSource) return;
        if (!MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated ||
            !MonoBehaviourSingleton<BetterAudio>.Instantiated)
        {
            WaitForSpatialInitialization(binding);
            return;
        }

        var spatial = MonoBehaviourSingleton<SpatialAudioSystem>.Instance;
        var audio = MonoBehaviourSingleton<BetterAudio>.Instance;
        if (!spatial || !audio)
        {
            WaitForSpatialInitialization(binding);
            return;
        }
        if (!SpatialAudioSystem.Initialized)
        {
            WaitForSpatialInitialization(binding);
            return;
        }

        var storage = RoomStorageField.GetValue(spatial) as AudioRoomStorage;
        if (storage is null) throw new InvalidOperationException("SpatialAudioSystem is initialized without AudioRoomStorage");

        // HandlerEnvironmentAudioSource treats a missing room as outdoor.
        // Keep that source default rather than turning an unknown room into
        // indoor occlusion.
        var room = storage.FindInitialRoomOptimized(binding.Marker.transform.position);
        var isOutdoor = room is null || room.IsOutdoor;
        var mixer = ResolveMixer(binding.Marker, audio, isOutdoor);
        binding.AudioSource.outputAudioMixerGroup = mixer;

        if (!binding.Marker.UseIndoorOcclusion || isOutdoor)
        {
            binding.Initialized = true;
            return;
        }

        BetterSource? wrapper = null;
        try
        {
            wrapper = audio.CreateBetterSourceWithParentSettings<SimpleSource>(
                binding.AudioSource, BetterAudio.AudioSourceGroupType.Environment, true, true);
            if (!wrapper) throw new InvalidOperationException("BetterAudio did not create an environment source wrapper");

            // This null mixer assignment is present in the source native
            // handler after wrapper creation.  The authored/raw source mixer
            // was selected above; retain the exact wrapper call order.
            wrapper!.SetMixerGroup(null);
            wrapper.Play(binding.AudioSource.clip, null, 0f, 1f, !binding.AudioSource.spatialize, false);
            binding.ContainerId = spatial.ProcessSourceOcclusion(
                binding.Marker.gameObject, wrapper, EOcclusionTest.ContinuousPropagated,
                30000f, Vector3.zero, true);
            if (binding.ContainerId < 0) throw new InvalidOperationException("Environment source occlusion registration failed");
            binding.BetterSource = wrapper;
            binding.Initialized = true;
        }
        catch
        {
            if (wrapper) wrapper!.Release();
            throw;
        }
    }

    private static AudioMixerGroup? ResolveMixer(EnvironmentAudioSource marker, BetterAudio audio, bool isOutdoor)
    {
        var authored = isOutdoor ? marker.OutdoorMixerGroup : marker.IndoorMixerGroup;
        if (authored != null && authored is not AudioMixerGroup)
            throw new InvalidOperationException("Environment mixer reference is not an AudioMixerGroup: " + marker.name);
        if (authored is AudioMixerGroup group) return group;
        var fallback = (isOutdoor ? AmbientOutMixerField : AmbientInMixerField).GetValue(audio);
        return fallback as AudioMixerGroup;
    }
}
