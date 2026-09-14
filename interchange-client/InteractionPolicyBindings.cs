using System;
using System.Collections;
using System.Reflection;
using EFT.Interactive;
using HarmonyLib;
using Manimal.Interchange.Components;
using SPT.Reflection.Patching;

namespace Manimal.Interchange.Client;

internal static class InteractionPolicyBindings
{
    private static readonly FieldInfo OpenOnUnlock = AccessTools.Field(typeof(KeycardDoor), "_openOnUnlock");

    internal static bool TryGet(WorldInteractiveObject owner, out InteractionPolicy policy) =>
        owner.TryGetComponent(out policy) && policy.Owner == owner;

    internal static void InitializeDoor(Door owner)
    {
        if (!TryGet(owner, out var policy) || policy.RelatedTrigger is not PhysicsTriggerHandler trigger) return;
        // Retail Door.OnAwake disables both the related handler and its collider.
        trigger.enabled = false;
        if (trigger.trigger) trigger.trigger.enabled = false;
    }

    internal static IEnumerator FinishUnlock(KeycardDoor owner, InteractionPolicy policy, IEnumerator original)
    {
        try
        {
            while (original.MoveNext()) yield return original.Current;
        }
        finally { (original as IDisposable)?.Dispose(); }

        // This runs after the native handle/beep sequence dispatches Open.
        // Cancellation/disposal does not reach this completion-only operation.
        if (owner && policy && policy.Owner == owner && policy.LockOperationsAfterOpen && (bool)OpenOnUnlock.GetValue(owner))
            owner.Operatable = false;
    }

    internal static string? InteractionLog(WorldInteractiveObject owner, InteractionResult result)
    {
        if (!TryGet(owner, out var policy) || !policy.LogInteractions) return null;
        return "Switch log: Interaction: " + result.InteractionType + " | " + policy.InteractionLogLabel;
    }
}

internal sealed class DoorRelatedTriggerPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(WorldInteractiveObject), "OnAwake");
    [PatchPostfix]
    private static void Postfix(WorldInteractiveObject __instance)
    {
        if (__instance is Door door) InteractionPolicyBindings.InitializeDoor(door);
    }
}

internal sealed class KeycardOperationPolicyPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(KeycardDoor), "UnlockCoroutine");
    [PatchPostfix]
    private static void Postfix(KeycardDoor __instance, ref IEnumerator __result)
    {
        if (InteractionPolicyBindings.TryGet(__instance, out var policy) && policy.LockOperationsAfterOpen)
            __result = InteractionPolicyBindings.FinishUnlock(__instance, policy, __result);
    }
}

internal sealed class SwitchInteractionLogPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Switch), "Interact", new[] { typeof(InteractionResult) });
    [PatchPrefix]
    private static void Prefix(Switch __instance, InteractionResult __0)
    {
        var message = InteractionPolicyBindings.InteractionLog(__instance, __0);
        if (message != null) Plugin.Log.LogInfo(message);
    }
}
