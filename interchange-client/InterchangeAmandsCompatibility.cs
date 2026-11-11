using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine.SceneManagement;

namespace Manimal.Interchange.Client;

// Amands recognizes stock scene names. Falling back to "!settings" on our
// renamed scene throws after AddSettings<MotionBlur>(), leaving that new effect
// enabled before the user's Off preference is applied.
internal static class InterchangeAmandsCompatibility
{
    private const string NativeScene = "Shopping_Mall_Scripts";
    private static FieldInfo? _sceneNames;
    private static FieldInfo? _scene;
    private static ActivationPatch? _activationPatch;
    private static UpdatePatch? _updatePatch;

    internal static void EnableIfAvailable()
    {
        if (_activationPatch is not null || _updatePatch is not null)
        {
            _activationPatch?.EnableIfInactive();
            _updatePatch?.EnableIfInactive();
            return;
        }

        var type = AccessTools.TypeByName("AmandsGraphics.AmandsGraphicsClass");
        if (type is null) return;
        _sceneNames = AccessTools.Field(type, "sceneLevelSettings");
        _scene = AccessTools.Field(type, "scene");
        var activate = AccessTools.Method(type, "ActivateAmandsGraphics");
        var update = AccessTools.Method(type, "UpdateAmandsGraphics");
        if (_sceneNames?.FieldType != typeof(Dictionary<string, string>) || _scene?.FieldType != typeof(string)
            || activate is null || update is null)
        {
            Plugin.Log.LogWarning("Interchange: unrecognized Amands scene API; compatibility patches were not installed.");
            return;
        }
        _activationPatch = new ActivationPatch(activate);
        _updatePatch = new UpdatePatch(update);
        try
        {
            _activationPatch.Enable();
            _updatePatch.Enable();
        }
        catch
        {
            Disable();
            throw;
        }
    }

    internal static void Disable()
    {
        TryDisable(_updatePatch);
        TryDisable(_activationPatch);
    }

    private static void TryDisable(ModulePatch? patch)
    {
        if (patch is null) return;
        try { patch.Disable(); }
        catch (Exception error) { if (Plugin.Log != null) Plugin.Log.LogError("Interchange Amands patch teardown failed: " + error); }
    }

    private static bool OwnsActiveScene()
    {
        var scene = SceneManager.GetActiveScene();
        return scene.name == NativeScene + "_MI" && SceneLoader.Owns(scene.path);
    }

    private sealed class ActivationPatch : ModulePatch
    {
        private readonly MethodBase _target;
        internal ActivationPatch(MethodBase target) : base(PatchIdentity.For(typeof(ActivationPatch))) => _target = target;
        protected override MethodBase GetTargetMethod() => _target;
        internal void EnableIfInactive() { if (!IsActive) Enable(); }
        [PatchPrefix]
        private static void Prefix(object __instance)
        {
            if (!OwnsActiveScene()) return;
            if (_sceneNames!.GetValue(__instance) is Dictionary<string, string> names
                && names.TryGetValue(NativeScene, out var settings))
                names[SceneManager.GetActiveScene().name] = settings;
        }
    }

    private sealed class UpdatePatch : ModulePatch
    {
        private readonly MethodBase _target;
        internal UpdatePatch(MethodBase target) : base(PatchIdentity.For(typeof(UpdatePatch))) => _target = target;
        protected override MethodBase GetTargetMethod() => _target;
        internal void EnableIfInactive() { if (!IsActive) Enable(); }
        [PatchPrefix]
        private static void Prefix(object __instance)
        {
            if (OwnsActiveScene()) _scene!.SetValue(__instance, NativeScene);
        }
    }
}
