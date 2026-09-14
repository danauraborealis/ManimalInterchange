using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using Manimal.Interchange.Components;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.Interchange.Client;

internal sealed class CloudLayerPatch : ModulePatch
{
    internal static readonly Type ControllerType = typeof(GameWorld).Assembly.GetType("EFT.Rendering.Clouds.CloudController", true);
    internal static readonly FieldInfo Renderer = AccessTools.Field(ControllerType, "_renderer");
    internal static readonly Type RendererType = Renderer.FieldType;
    internal static readonly FieldInfo Material = AccessTools.Field(RendererType, "_cloudLayerMaterial");
    internal static readonly FieldInfo PropertyBlock = AccessTools.Field(RendererType, "_propertyBlock");
    internal static readonly int Stretch = Shader.PropertyToID("_Params4");

    protected override MethodBase GetTargetMethod() => AccessTools.Method(ControllerType, "OnEnable", Type.EmptyTypes);

    [PatchPostfix]
    private static void Postfix(MonoBehaviour __instance)
    {
        if (!__instance.TryGetComponent<CloudLayerPolicy>(out var policy) || policy.Controller != __instance) return;
        if (!policy.PixelShader || !policy.PixelShader.isSupported || float.IsNaN(policy.StretchDistance) || float.IsInfinity(policy.StretchDistance))
            throw new InvalidOperationException("Invalid Interchange cloud shader or stretch setting: " + policy.SourceKey);
        var renderer = Renderer.GetValue(__instance) ?? throw new InvalidOperationException("Native cloud renderer was not initialized");
        var material = (Material)Material.GetValue(renderer);
        var block = (MaterialPropertyBlock)PropertyBlock.GetValue(renderer);
        // Native OnEnable creates a private renderer/material for this controller.
        // Native OnDisable owns their cleanup, including repeated enable cycles.
        var queue = material.renderQueue;
        material.shader = policy.PixelShader;
        material.renderQueue = queue;
        // The retail draw supplies this vector globally. A per-renderer block
        // supplies the same shader input without changing other cameras' state.
        block.SetVector(Stretch, new Vector4(policy.StretchDistance, 0, 0, 0));
    }
}
