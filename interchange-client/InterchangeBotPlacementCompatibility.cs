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
    private static NonWavesZonePatch? _nonWavesZonePatch;
    private static SpawnCachePatch? _spawnCachePatch;
    // Track only the BotZone instances this compatibility layer adds. BPS owns
    // this process-wide list too, so teardown must not clear or restore entries
    // written by BPS or another map after our scene has gone away.
    private static readonly HashSet<BotZone> OwnedZones = new();

    internal static void EnableIfAvailable()
    {
        if (_nonWavesZonePatch is not null || _spawnCachePatch is not null)
        {
            if (_nonWavesZonePatch is not null && !_nonWavesZonePatch.IsActive) _nonWavesZonePatch.Enable();
            if (_spawnCachePatch is not null && !_spawnCachePatch.IsActive) _spawnCachePatch.Enable();
            return;
        }

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

    internal static void Disable()
    {
        TryDisable(_spawnCachePatch);
        TryDisable(_nonWavesZonePatch);
        ReleaseOwnedZoneCache();
    }

    private static void ReleaseOwnedZoneCache()
    {
        try
        {
            if (_cachedNonSnipeZones?.GetValue(null) is List<BotZone> cache)
            {
                for (var index = cache.Count - 1; index >= 0; index--)
                {
                    if (OwnedZones.Contains(cache[index])) cache.RemoveAt(index);
                }
            }
        }
        catch (Exception error)
        {
            if (Plugin.Log != null) Plugin.Log.LogError("Interchange BPS zone-cache teardown failed: " + error);
        }
        finally
        {
            // Even if BPS replaced its cache or reflection became invalid,
            // never retain scene objects in this compatibility layer.
            OwnedZones.Clear();
        }
    }

    private static void TryDisable(ModulePatch? patch)
    {
        if (patch is null || !patch.IsActive) return;
        try { patch.Disable(); }
        catch (Exception error) { if (Plugin.Log != null) Plugin.Log.LogError("Interchange BPS patch teardown failed: " + error); }
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

        var cache = _cachedNonSnipeZones.GetValue(null) as List<BotZone>;
        if (cache is null)
        {
            cache = new List<BotZone>();
            _cachedNonSnipeZones.SetValue(null, cache);
        }

        // Remove only the prior Interchange contribution. This leaves BPS's
        // own entries and any later map's entries intact if a scene transition
        // races with compatibility teardown.
        for (var index = cache.Count - 1; index >= 0; index--)
        {
            if (OwnedZones.Contains(cache[index])) cache.RemoveAt(index);
        }
        OwnedZones.Clear();

        foreach (var zone in zones)
        {
            if (cache.Contains(zone)) continue;
            cache.Add(zone);
            OwnedZones.Add(zone);
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

    private sealed class NonWavesZonePatch : ModulePatch
    {
        private readonly MethodBase _target;
        internal NonWavesZonePatch(MethodBase target) : base(PatchIdentity.For(typeof(NonWavesZonePatch))) => _target = target;
        protected override MethodBase GetTargetMethod() => _target;

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

    private sealed class SpawnCachePatch : ModulePatch
    {
        private readonly MethodBase _target;
        internal SpawnCachePatch(MethodBase target) : base(PatchIdentity.For(typeof(SpawnCachePatch))) => _target = target;
        protected override MethodBase GetTargetMethod() => _target;

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
