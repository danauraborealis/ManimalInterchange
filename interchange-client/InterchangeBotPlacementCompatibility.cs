using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Game.Spawning;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine.SceneManagement;

namespace Manimal.Interchange.Client;

/// <summary>
/// Keeps BPS's scav-only zone cache aligned with the location's open zones.
/// BPS uses every non-snipe scene BotZone for its non-wave selector, while
/// vanilla already exposes the server-filtered list through _openedZones.
/// </summary>
internal static class InterchangeBotPlacementCompatibility
{
    private const string UtilityTypeName = "BotPlacementSystemClient.Utils.Utility";
    private const string NonWavesPatchTypeName = "BotPlacementSystemClient.Patches.NonWavesSpawnSystemPatch";

    private static FieldInfo? _cachedNonSnipeZones;
    private static ModulePatch? _nonWavesZonePatch;
    private static ModulePatch? _spawnCachePatch;
    private static bool _initialized;

    internal static void EnableIfAvailable()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        var utilityType = FindLoadedType(UtilityTypeName);
        if (utilityType is null)
        {
            // BPS is optional.  There is no client compatibility work to do
            // when its assembly is absent.
            return;
        }

        _cachedNonSnipeZones = utilityType.GetField(
            "CachedNonSnipeZones",
            BindingFlags.Public | BindingFlags.Static);
        if (_cachedNonSnipeZones is null || _cachedNonSnipeZones.FieldType != typeof(List<BotZone>))
        {
            Plugin.Log.LogWarning("Interchange: BPS client zone cache API was not recognized; leaving BPS client spawning unchanged.");
            _cachedNonSnipeZones = null;
            return;
        }

        var nonWavesType = FindLoadedType(NonWavesPatchTypeName);
        var nonWavesMethod = nonWavesType?.GetMethod(
            "GetValidBotZone",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types:
            [
                typeof(WildSpawnType),
                typeof(int),
                typeof(BotZone[]),
                typeof(string),
                typeof(BotsController)
            ],
            modifiers: null);

        if (nonWavesMethod is not null)
        {
            _nonWavesZonePatch = new NonWavesZonePatch(nonWavesMethod);
            _nonWavesZonePatch.Enable();
        }

        var spawnMethod = typeof(BotSpawner).GetMethod(
            nameof(BotSpawner.TryToSpawnInZoneAndDelay),
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types:
            [
                typeof(BotZone),
                typeof(BotCreationData),
                typeof(bool),
                typeof(bool),
                typeof(List<ISpawnPoint>),
                typeof(bool)
            ],
            modifiers: null);
        if (spawnMethod is not null)
        {
            _spawnCachePatch = new SpawnCachePatch(spawnMethod);
            _spawnCachePatch.Enable();
        }

        if (_nonWavesZonePatch is null && _spawnCachePatch is null)
        {
            Plugin.Log.LogWarning("Interchange: BPS client zone selector API was not recognized; leaving BPS client spawning unchanged.");
            return;
        }

        Plugin.Log.LogInfo("Interchange: BPS client scav selectors use Interchange _openedZones; dedicated boss zones remain available to boss spawning.");
    }

    private static Type? FindLoadedType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, throwOnError: false);
                if (type is not null)
                {
                    return type;
                }
            }
            catch (ReflectionTypeLoadException)
            {
                // An unrelated partially loadable assembly must not stop the
                // optional BPS lookup.
            }
        }

        return null;
    }

    private static bool IsOwnedInterchangeRaid()
    {
        if (!Singleton<GameWorld>.Instantiated)
        {
            return false;
        }

        var gameWorld = Singleton<GameWorld>.Instance;
        if (gameWorld is null || !string.Equals(gameWorld.LocationId, "interchange", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var index = 0; index < SceneManager.sceneCount; index++)
        {
            var scene = SceneManager.GetSceneAt(index);
            if (SceneLoader.Owns(scene.path))
            {
                return true;
            }
        }

        return false;
    }

    private static bool RefreshScavZoneCache(BotSpawner? spawner)
    {
        if (!IsOwnedInterchangeRaid() || spawner is null || spawner._openedZones is null || spawner._openedZones.Length == 0 || _cachedNonSnipeZones is null)
        {
            return false;
        }

        var zones = new List<BotZone>();
        foreach (var zone in spawner._openedZones)
        {
            if (zone is null || zone.SnipeZone || !HasBotSpawnPoints(zone))
            {
                continue;
            }

            zones.Add(zone);
        }

        if (zones.Count == 0)
        {
            return false;
        }

        if (_cachedNonSnipeZones.GetValue(null) is List<BotZone> cache)
        {
            cache.Clear();
            cache.AddRange(zones);
        }
        else
        {
            _cachedNonSnipeZones.SetValue(null, zones);
        }

        return true;
    }

    private static bool HasBotSpawnPoints(BotZone zone)
    {
        foreach (var point in zone.SpawnPoints)
        {
            if (point is null || point.Categories != ESpawnCategoryMask.All && !point.Categories.ContainBotCategory())
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private sealed class NonWavesZonePatch(MethodBase target) : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => target;

        [PatchPrefix]
        private static void Prefix(ref BotZone[] allZones, BotsController _botsController)
        {
            var spawner = _botsController?.BotSpawner;
            if (spawner is null || !RefreshScavZoneCache(spawner))
            {
                return;
            }

            var openZones = spawner._openedZones;
            if (openZones is null || openZones.Length == 0)
            {
                return;
            }

            var filtered = new List<BotZone>();
            foreach (var zone in openZones)
            {
                if (zone is null || zone.SnipeZone || !HasBotSpawnPoints(zone))
                {
                    continue;
                }

                filtered.Add(zone);
            }

            if (filtered.Count != 0)
            {
                allZones = filtered.ToArray();
            }
        }
    }

    private sealed class SpawnCachePatch(MethodBase target) : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => target;

        [HarmonyPriority(Priority.First)]
        [PatchPrefix]
        private static void Prefix(BotSpawner __instance)
        {
            if (__instance is not null)
            {
                RefreshScavZoneCache(__instance);
            }
        }
    }
}
