using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Manimal.Interchange.Components;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.Interchange.Client;

internal static class NativeCameraBindings
{
    internal const string AssetPath = "camera/sharedassets15.assets/691";
    internal static Camera Resolve()
    {
        AssetBundle? found = null;
        foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
        {
            if (!bundle) continue;
            foreach (var name in bundle.GetAllAssetNames())
            {
                if (name != AssetPath) continue;
                if (found) throw new InvalidDataException("Multiple native camera reference bundles are loaded");
                found = bundle;
            }
        }
        if (!found) throw new InvalidOperationException("Native camera reference bundle must load before map scenes");
        var camera = found!.LoadAsset<Camera>(AssetPath);
        if (!camera || camera.gameObject.scene.IsValid()) throw new InvalidDataException("Native map camera reference did not resolve to a prefab");
        return camera;
    }
    internal static bool Apply(LevelSettings owner)
    {
        if (!owner.TryGetComponent<LevelLightingPolicy>(out var policy) || policy.NativeLevelSettings != owner || !policy.UseNativeCameraPrefab) return false;
        owner.CameraPrefab = Resolve();
        return true;
    }
}

internal sealed class NativeCameraPrefabPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(LevelSettings), "Awake");
    [PatchPrefix]
    private static void Prefix(LevelSettings __instance) => NativeCameraBindings.Apply(__instance);
}
