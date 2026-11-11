using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using EFT;
using EFT.AssetsManager;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine.SceneManagement;

namespace Manimal.Interchange.Client;

internal sealed class LoadPresetPatch : ModulePatch
{
    internal LoadPresetPatch() : base(PatchIdentity.For(typeof(LoadPresetPatch))) { }
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(LoadScenesFromPresetOperation), nameof(LoadScenesFromPresetOperation.LoadPresetAsync));
    [PatchPrefix]
    private static bool Prefix(LoadScenesFromPresetOperation __instance, ScenesPreset preset, ref Task __result)
        => SceneLoader.BeforeLoad(__instance, preset, ref __result);
}

internal sealed class LoadPresetReversePatch : ModulePatch
{
    internal LoadPresetReversePatch() : base(PatchIdentity.For(typeof(LoadPresetReversePatch))) { }
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(LoadScenesFromPresetOperation), nameof(LoadScenesFromPresetOperation.LoadPresetAsync));
    [PatchReverse, MethodImpl(MethodImplOptions.NoInlining)]
    internal static Task LoadOriginal(LoadScenesFromPresetOperation instance, ScenesPreset preset)
        => throw new NotSupportedException("SPT reverse patch was not initialized");
}

internal sealed class BundledScenePatch : ModulePatch
{
    internal BundledScenePatch() : base(PatchIdentity.For(typeof(BundledScenePatch))) { }
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(AssetsManagerExtension), nameof(AssetsManagerExtension.LoadScene));
    [PatchPrefix]
    private static bool Prefix(ResourceKey resourceKey, LoadSceneMode loadSceneMode, bool allowSceneActivation, Action<float> progressCallback, ref LoadSceneOperation __result)
        => SceneLoader.BeforeSceneLoad(resourceKey, loadSceneMode, allowSceneActivation, progressCallback, ref __result);
}
