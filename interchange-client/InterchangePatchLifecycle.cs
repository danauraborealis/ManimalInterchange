using System;
using System.Collections.Generic;
using SPT.Reflection.Patching;

namespace Manimal.Interchange.Client;

/// <summary>
/// Owns the patches that only have meaning while replacement content is
/// acquired. The preset interception remains global so native menu loads can
/// still be observed without touching gameplay methods off-map.
/// </summary>
internal static class InterchangePatchLifecycle
{
    private static readonly ModulePatch[] RaidPatches =
    {
        new BundledScenePatch(),
        new CullingSidecarPatch(),
        new AudioBakeSidecarPatch(),
        new AcousticMapPathPatch(),
        new AcousticMapLoadPatch(),
        new AcousticGeometryPathPatch(),
        new AcousticGeometryLoadPatch(),
        new RoomGeometryPatch(),
        new AreaLightInitPatch(),
        new AmbientBlendSystemInitPatch(),
        new TerrainSeasonPatch(),
        new BallisticTriggerPatch(),
        new CloudLayerPatch(),
        new EffectSoundPolicyPatch(),
        new DistanceAudioFadePatch(),
        new OwnedSoundGagPatch(),
        new AdvancedSoundPolicyPatch(),
        new FlareExitEventPatch(),
        new FlareExitQueuedNotificationPatch(),
        new DoorRelatedTriggerPatch(),
        new KeycardOperationPolicyPatch(),
        new SwitchInteractionLogPatch(),
        new SyncLoopInitPatch(),
        new SyncLoopPlayPatch(),
        new SyncLoopStopPatch(),
        new WaterRendererPatch(),
        new DistantShadowCacheParametersPatch(),
        new DistantShadowOrthoMatrixPatch(),
        new DistantShadowTransformPatch(),
        new NativeCameraPrefabPatch()
    };

    private static readonly List<ModulePatch> EnabledRaidPatches = new();
    private static LoadPresetReversePatch? _loadPresetReversePatch;
    private static LoadPresetPatch? _loadPresetPatch;
    private static bool _presetHooksEnabled;

    internal static bool RaidPatchesEnabled { get; private set; }
    internal static int ActiveRaidPatchCount => EnabledRaidPatches.Count;

    internal static void EnablePresetHooks()
    {
        if (_presetHooksEnabled) return;
        var attempted = new List<ModulePatch>(2);
        try
        {
            _loadPresetReversePatch ??= new LoadPresetReversePatch();
            if (!_loadPresetReversePatch.IsActive)
            {
                attempted.Add(_loadPresetReversePatch);
                _loadPresetReversePatch.Enable();
            }

            _loadPresetPatch ??= new LoadPresetPatch();
            if (!_loadPresetPatch.IsActive)
            {
                attempted.Add(_loadPresetPatch);
                _loadPresetPatch.Enable();
            }

            _presetHooksEnabled = true;
        }
        catch
        {
            DisablePatches(attempted);
            throw;
        }
    }

    internal static void DisablePresetHooks()
    {
        _presetHooksEnabled = false;
        TryDisable(_loadPresetPatch);
        TryDisable(_loadPresetReversePatch);
    }

    internal static void Enable()
    {
        if (RaidPatchesEnabled) return;
        var attempted = new List<ModulePatch>(RaidPatches.Length);
        try
        {
            for (var i = 0; i < RaidPatches.Length; i++)
            {
                var patch = RaidPatches[i];
                if (patch.IsActive)
                {
                    EnabledRaidPatches.Add(patch);
                    continue;
                }

                attempted.Add(patch);
                patch.Enable();
                EnabledRaidPatches.Add(patch);
            }

            InterchangeAmandsCompatibility.EnableIfAvailable();
            InterchangeWaypointsCompatibility.EnableIfAvailable();
            InterchangeBotPlacementCompatibility.EnableIfAvailable();
            RaidPatchesEnabled = true;
        }
        catch
        {
            InterchangeBotPlacementCompatibility.Disable();
            InterchangeWaypointsCompatibility.Disable();
            InterchangeAmandsCompatibility.Disable();
            DisablePatches(attempted);
            EnabledRaidPatches.Clear();
            RaidPatchesEnabled = false;
            throw;
        }
    }

    internal static void Disable()
    {
        RaidPatchesEnabled = false;
        for (var i = RaidPatches.Length - 1; i >= 0; i--)
        {
            var patch = RaidPatches[i];
            if (patch.IsActive) TryDisable(patch);
        }
        EnabledRaidPatches.Clear();
        InterchangeBotPlacementCompatibility.Disable();
        InterchangeWaypointsCompatibility.Disable();
        InterchangeAmandsCompatibility.Disable();
        AudioBindings.ReleaseRaid();
        // File verification is a process-session cache. Clear it after the
        // owned scene/bundle teardown so hot-swapped payload revisions cannot
        // accumulate keys for every completed raid.
        InterchangeVerifiedFiles.Clear();
    }

    private static void DisablePatches(List<ModulePatch> patches)
    {
        for (var i = patches.Count - 1; i >= 0; i--) TryDisable(patches[i]);
    }

    private static void TryDisable(ModulePatch? patch)
    {
        if (patch is null) return;
        try { patch.Disable(); }
        catch (Exception error)
        {
            if (Plugin.Log != null) Plugin.Log.LogError("Interchange patch teardown failed: " + error);
        }
    }
}
