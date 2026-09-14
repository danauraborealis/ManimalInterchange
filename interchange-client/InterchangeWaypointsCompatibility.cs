using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using SPT.Reflection.Patching;
using UnityEngine.SceneManagement;

namespace Manimal.Interchange.Client;

// Waypoints replaces the whole scene navmesh with its own baked
// interchange-navmesh.bundle at BotsController.Init. That bake is the old
// layout and has no coverage for the reworked areas, so bots that spawn there
// stand still. Skip only the navmesh swap on owned Interchange raids; the
// door-link and path patches work from the live scene and stay active.
internal static class InterchangeWaypointsCompatibility
{
    private const string WaypointPatchTypeName = "DrakiaXYZ.Waypoints.Patches.WaypointPatch";
    private static ModulePatch? _patch;
    private static bool _initialized;

    internal static void EnableIfAvailable()
    {
        if (_initialized) return;
        _initialized = true;
        var patchType = FindLoadedType(WaypointPatchTypeName);
        if (patchType is null) return;
        var inject = patchType.GetMethod("InjectNavmesh", BindingFlags.NonPublic | BindingFlags.Static, null, [typeof(GameWorld)], null);
        if (inject is null)
        {
            Plugin.Log.LogWarning("Interchange: Waypoints navmesh injection API was not recognized; its custom navmesh will still replace the reworked map's.");
            return;
        }
        _patch = new InjectNavmeshPatch(inject);
        _patch.Enable();
        Plugin.Log.LogInfo("Interchange: Waypoints custom navmesh is skipped on the reworked Interchange; the scene's own navmesh is used.");
    }

    private static Type? FindLoadedType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, throwOnError: false);
                if (type is not null) return type;
            }
            catch (ReflectionTypeLoadException)
            {
                // an unrelated partially loadable assembly must not stop the optional lookup
            }
        }
        return null;
    }

    private static bool IsOwnedInterchangeRaid(GameWorld? gameWorld)
    {
        if (!gameWorld || !string.Equals(gameWorld!.LocationId, "interchange", StringComparison.OrdinalIgnoreCase)) return false;
        for (var i = 0; i < SceneManager.sceneCount; i++)
        {
            if (SceneLoader.Owns(SceneManager.GetSceneAt(i).path)) return true;
        }
        return false;
    }

    private sealed class InjectNavmeshPatch(MethodBase target) : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => target;

        [PatchPrefix]
        private static bool Prefix(GameWorld gameWorld)
        {
            if (!IsOwnedInterchangeRaid(gameWorld)) return true;
            Plugin.Log.LogInfo("Interchange: skipped Waypoints navmesh replacement for the reworked map.");
            return false;
        }
    }
}
