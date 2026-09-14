using System;
using System.Reflection;
using EFT;
using EFT.Ballistics;
using EFT.GameTriggers;
using HarmonyLib;
using Manimal.Interchange.Components;
using SPT.Reflection.Patching;

namespace Manimal.Interchange.Client;

internal sealed class BallisticTriggerPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(TriggerBallistic), "CG_Start", new[] { typeof(DamageInfo) });

    [PatchPrefix]
    private static bool Prefix(TriggerBallistic __instance, DamageInfo __0)
    {
        if (!__instance.TryGetComponent<BallisticTriggerPolicy>(out var policy) || policy.Trigger != __instance) return true;
        return policy.Allows(__0.DamageType == EDamageType.Melee, __0.Damage);
    }
}
