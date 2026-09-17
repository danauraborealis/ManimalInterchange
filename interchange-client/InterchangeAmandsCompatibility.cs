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

    internal static void EnableIfAvailable()
    {
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
        new ActivationPatch(activate).Enable();
        new UpdatePatch(update).Enable();
    }

    private static bool OwnsActiveScene()
    {
        var scene = SceneManager.GetActiveScene();
        return scene.name == NativeScene + "_MI" && SceneLoader.Owns(scene.path);
    }

    private sealed class ActivationPatch(MethodBase target) : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => target;
        [PatchPrefix]
        private static void Prefix(object __instance)
        {
            if (!OwnsActiveScene()) return;
            if (_sceneNames!.GetValue(__instance) is Dictionary<string, string> names
                && names.TryGetValue(NativeScene, out var settings))
                names[SceneManager.GetActiveScene().name] = settings;
        }
    }

    private sealed class UpdatePatch(MethodBase target) : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => target;
        [PatchPrefix]
        private static void Prefix(object __instance)
        {
            if (OwnsActiveScene()) _scene!.SetValue(__instance, NativeScene);
        }
    }
}
