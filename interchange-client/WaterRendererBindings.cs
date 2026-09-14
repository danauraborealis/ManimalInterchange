using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Manimal.Interchange.Components;
using SPT.Reflection.Patching;
using UnityEngine;
using WaterSSR;

namespace Manimal.Interchange.Client;

/// <summary>
/// Supplies the one serialized dependency that the projected renderer cannot
/// carry in a converted scene. WaterRendererv3 continues to own its material,
/// command buffer and camera callback lifecycle.
/// </summary>
internal static class WaterRendererBindings
{
    internal const string NativeShaderAssetPath = "shader/sharedassets15.assets/679";
    internal const string NativeShaderName = "Hidden/WaterForSSRv2";
    internal const string NativeShaderSourceFile = "sharedassets15.assets";
    internal const long NativeShaderPathId = 679;

    private static readonly FieldInfo ShaderField = AccessTools.Field(typeof(WaterRendererv3), "_shader")
        ?? throw new MissingFieldException(typeof(WaterRendererv3).FullName, "_shader");

    internal static void BeforeEnable(WaterRendererv3 renderer)
    {
        if (!renderer.TryGetComponent<WaterRendererPolicy>(out var policy)) return;
        if (!policy.enabled) throw new InvalidOperationException("Water renderer policy is disabled: " + policy.SourceKey);
        if (policy.Owner != renderer)
            throw new InvalidOperationException("Water renderer policy owner differs: " + policy.SourceKey);
        if (string.IsNullOrEmpty(policy.SourceKey) || string.IsNullOrEmpty(policy.SourceWaterPropertiesSha256) ||
            policy.SourceWaterPropertiesSha256.Length != 64)
            throw new InvalidDataException("Water renderer policy identity is incomplete: " + policy.SourceKey);
        if (policy.SourceResolution != 1 && policy.SourceResolution != 2 && policy.SourceResolution != 4 && policy.SourceResolution != 8)
            throw new InvalidDataException("Unsupported retail water resolution: " + policy.SourceResolution);
        if (policy.UnderwaterEnabled || policy.DynamicFoamEnabled)
            throw new InvalidDataException("Retail water features require a separate adapter: " + policy.SourceKey);
        if (policy.BlurIterations < 2 || policy.BlurIterations > 8 || float.IsNaN(policy.WaterLevel) || float.IsInfinity(policy.WaterLevel))
            throw new InvalidDataException("Invalid retail water property evidence: " + policy.SourceKey);

        var shader = ResolveNativeShader(policy.SourceKey);
        ShaderField.SetValue(renderer, shader);
    }

    private static Shader ResolveNativeShader(string sourceKey)
    {
        AssetBundle? match = null;
        var matches = 0;
        foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
        {
            if (!bundle) continue;
            var names = bundle.GetAllAssetNames();
            for (var i = 0; i < names.Length; i++)
            {
                if (!string.Equals(names[i], NativeShaderAssetPath, StringComparison.OrdinalIgnoreCase)) continue;
                match = bundle;
                matches++;
                break;
            }
        }
        var referenceBundle = match;
        if (matches != 1 || referenceBundle == null || !referenceBundle)
            throw new InvalidOperationException("Native water shader reference bundle is not uniquely loaded for " + sourceKey);

        var shader = referenceBundle.LoadAsset<Shader>(NativeShaderAssetPath);
        if (!shader) throw new InvalidOperationException("Native water shader asset failed to load for " + sourceKey);
        if (!string.Equals(shader.name, NativeShaderName, StringComparison.Ordinal))
            throw new InvalidDataException("Native water shader identity differs: " + shader.name);
        if (!shader.isSupported)
            throw new InvalidOperationException("Native water shader is unsupported on this GPU: " + NativeShaderName);
        return shader;
    }
}

/// <summary>
/// The SPT patch wrapper runs before Unity invokes WaterRendererv3.OnEnable.
/// </summary>
internal sealed class WaterRendererPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(WaterRendererv3), "OnEnable", Type.EmptyTypes)
        ?? throw new MissingMethodException(typeof(WaterRendererv3).FullName, "OnEnable");

    [PatchPrefix]
    private static void Prefix(WaterRendererv3 __instance) => WaterRendererBindings.BeforeEnable(__instance);
}
