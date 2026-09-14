using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using HarmonyLib;
using Manimal.Interchange.Components;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Interchange.Client;

/// <summary>
/// Applies the source LevelSettings distant-shadow values at the target's
/// native read points. The policy marker owns the values; this class owns only
/// the temporary field overrides and restores them when that owner leaves.
/// </summary>
internal static class LevelLightingBindings
{
    private sealed class ShadowClaim
    {
        internal readonly LevelLightingPolicy Policy;
        internal readonly float Offset;
        internal readonly float FarPlane;

        internal ShadowClaim(LevelLightingPolicy policy, float offset, float farPlane)
        {
            Policy = policy;
            Offset = offset;
            FarPlane = farPlane;
        }
    }

    private static readonly List<LevelLightingPolicy> Policies = new();
    private static readonly Dictionary<DistantShadow, ShadowClaim> Claims = new();
    private static bool _enabled;
    private static bool _patchesEnabled;

    internal static int ActivePolicyCount => Policies.Count;
    internal static int TrackedShadowCount => Claims.Count;

    internal static void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        LevelLightingPolicy.Enabled += Register;
        LevelLightingPolicy.Disabled += Unregister;
        LevelLightingPolicy.Destroyed += Unregister;
        SceneManager.sceneUnloaded += SceneUnloaded;
        if (!_patchesEnabled)
        {
            new DistantShadowCacheParametersPatch().Enable();
            new DistantShadowOrthoMatrixPatch().Enable();
            new DistantShadowTransformPatch().Enable();
            _patchesEnabled = true;
        }

        // A scene can load before the client binding receives its first event.
        // Recover only active scene policies; Resources also returns prefab
        // assets, which are intentionally ignored here.
        foreach (var policy in Resources.FindObjectsOfTypeAll<LevelLightingPolicy>())
        {
            if (policy && policy.isActiveAndEnabled && policy.gameObject.scene.IsValid()) Register(policy);
        }
    }

    internal static void Disable()
    {
        if (!_enabled) return;
        _enabled = false;
        LevelLightingPolicy.Enabled -= Register;
        LevelLightingPolicy.Disabled -= Unregister;
        LevelLightingPolicy.Destroyed -= Unregister;
        SceneManager.sceneUnloaded -= SceneUnloaded;
        ReleaseAll();
        Policies.Clear();
    }

    internal static void SceneUnloaded(Scene scene)
    {
        // Unregister before Unity destroys the marker objects so their last
        // owned values can still be restored on a live DistantShadow.
        var removed = new List<LevelLightingPolicy>();
        foreach (var policy in Policies)
        {
            if (!policy) { removed.Add(policy); continue; }
            var owner = Owner(policy);
            var policyScene = policy.gameObject.scene;
            var ownerScene = owner ? owner!.gameObject.scene : default;
            if ((policyScene.IsValid() && policyScene.handle == scene.handle) ||
                (owner && ownerScene.IsValid() && ownerScene.handle == scene.handle))
                removed.Add(policy);
        }
        foreach (var policy in removed) Unregister(policy);
    }

    internal static void BeforeShadowRead(DistantShadow shadow)
    {
        if (!shadow)
        {
            return;
        }

        var policy = FindOwner(shadow);
        if (!policy)
        {
            RestoreClaim(shadow);
            return;
        }
        Validate(policy!);

        if (Claims.TryGetValue(shadow, out var claim) && claim.Policy != policy)
        {
            // A global DistantShadow may survive a map transition. Release a
            // previous map's claim before allowing the current owner to take
            // it, so values cannot bleed between additive maps.
            RestoreClaim(shadow);
            claim = null;
        }
        if (claim == null)
        {
            claim = new ShadowClaim(policy!, shadow.OffsetTowardsLight, shadow.FarPlane);
            Claims[shadow] = claim;
        }
        shadow.OffsetTowardsLight = policy!.DistantShadowOffset;
        shadow.FarPlane = policy.DistantShadowFarPlane;
    }

    private static void Register(LevelLightingPolicy policy)
    {
        if (!_enabled || !policy || !policy.isActiveAndEnabled || !policy.gameObject.scene.IsValid()) return;
        if (Policies.Contains(policy)) return;
        Policies.Add(policy);
    }

    private static void Unregister(LevelLightingPolicy policy)
    {
        ReleaseClaims(policy);
        Policies.Remove(policy);
    }

    private static LevelSettings? Owner(LevelLightingPolicy policy)
    {
        var attached = policy.GetComponent<LevelSettings>();
        if (!attached) return null;
        var serialized = policy.NativeLevelSettings as LevelSettings;
        // The marker is authored on the native LevelSettings GameObject. A
        // missing, stale, or cross-scene PPtr is ignored instead of granting
        // it control over another map's global DistantShadow.
        if (!serialized || serialized != attached) return null;
        return attached;
    }

    private static LevelLightingPolicy? FindOwner(DistantShadow shadow)
    {
        if (!shadow.gameObject.scene.IsValid()) return null;
        var shadowScene = shadow.gameObject.scene.handle;
        LevelSettings? current = null;
        if (Singleton<LevelSettings>.Instantiated) current = Singleton<LevelSettings>.Instance;
        LevelLightingPolicy? sameScene = null;
        LevelLightingPolicy? currentOwner = null;
        foreach (var policy in Policies)
        {
            if (!policy || !policy.isActiveAndEnabled) continue;
            var owner = Owner(policy);
            if (!owner || !owner!.isActiveAndEnabled) continue;
            if (owner!.gameObject.scene.handle == shadowScene && policy.gameObject.scene.handle == shadowScene)
                sameScene = Prefer(sameScene, policy);
            if (current && owner == current) currentOwner = Prefer(currentOwner, policy);
        }
        return sameScene ?? currentOwner;
    }

    private static LevelLightingPolicy Prefer(LevelLightingPolicy? current, LevelLightingPolicy candidate)
    {
        if (!current) return candidate;
        var comparison = string.CompareOrdinal(candidate.SourceKey, current!.SourceKey);
        if (comparison < 0 || (comparison == 0 && candidate.GetInstanceID() < current.GetInstanceID())) return candidate;
        return current;
    }

    private static void Validate(LevelLightingPolicy policy)
    {
        if (float.IsNaN(policy.DistantShadowOffset) || float.IsInfinity(policy.DistantShadowOffset) || policy.DistantShadowOffset < 0f ||
            float.IsNaN(policy.DistantShadowFarPlane) || float.IsInfinity(policy.DistantShadowFarPlane) || policy.DistantShadowFarPlane <= 0f)
            throw new InvalidOperationException("Invalid LevelLightingPolicy distant-shadow values: " + policy.SourceKey);
    }

    private static void RestoreClaim(DistantShadow shadow)
    {
        if (!Claims.TryGetValue(shadow, out var claim)) return;
        if (shadow)
        {
            shadow.OffsetTowardsLight = claim.Offset;
            shadow.FarPlane = claim.FarPlane;
        }
        Claims.Remove(shadow);
    }

    private static void ReleaseClaims(LevelLightingPolicy policy)
    {
        var shadows = new List<DistantShadow>();
        foreach (var item in Claims)
        {
            if (item.Value.Policy == policy) shadows.Add(item.Key);
        }
        foreach (var shadow in shadows) RestoreClaim(shadow);
    }

    private static void ReleaseAll()
    {
        var shadows = new List<DistantShadow>(Claims.Keys);
        foreach (var shadow in shadows) RestoreClaim(shadow);
    }
}

internal sealed class DistantShadowCacheParametersPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var method = AccessTools.Method(typeof(DistantShadow), "CacheParameters", new[] { typeof(int) });
        if (method == null || method.ReturnType != typeof(void)) throw new MissingMethodException(typeof(DistantShadow).FullName, "CacheParameters(System.Int32)");
        return method;
    }

    [PatchPrefix]
    private static void Prefix(DistantShadow __instance) => LevelLightingBindings.BeforeShadowRead(__instance);
}

internal sealed class DistantShadowOrthoMatrixPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var method = AccessTools.Method(typeof(DistantShadow), "GetOrthoMatrix", Type.EmptyTypes);
        if (method == null || method.ReturnType != typeof(Matrix4x4)) throw new MissingMethodException(typeof(DistantShadow).FullName, "GetOrthoMatrix()");
        return method;
    }

    [PatchPrefix]
    private static void Prefix(DistantShadow __instance) => LevelLightingBindings.BeforeShadowRead(__instance);
}

internal sealed class DistantShadowTransformPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var method = AccessTools.Method(typeof(DistantShadow), "method_13", Type.EmptyTypes);
        if (method == null || method.ReturnType != typeof(void)) throw new MissingMethodException(typeof(DistantShadow).FullName, "method_13()");
        return method;
    }

    [PatchPrefix]
    private static void Prefix(DistantShadow __instance) => LevelLightingBindings.BeforeShadowRead(__instance);
}
