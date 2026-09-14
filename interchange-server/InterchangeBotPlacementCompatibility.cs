using System.Reflection;
using JetBrains.Annotations;
using Manimal.Interchange.Shared;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace Manimal.Interchange.Server;

/// <summary>
/// BPS retains ownership of its wave generation, configuration and raid rebuilds.
/// Only its legacy Interchange starter-scav zone lookup needs replacement: the
/// reworked map also has public Bot spawn points in ZoneBearCamp.
/// </summary>
[Injectable(InjectionType = InjectionType.Singleton, TypePriority = OnLoadOrder.Preload + 5), UsedImplicitly]
public sealed class InterchangeBotPlacementCompatibility(
    LocationTable locations,
    ISptLogger<InterchangeBotPlacementCompatibility> logger) : IOnLoad, IDisposable
{
    private const string ScavSpawnsType = "BotPlacementSystemServer.Controllers.ScavSpawns";
    private static InterchangeBotPlacementCompatibility? _active;
    private readonly LocationTable _locations = locations;
    private ScavZonePatch? _patch;

    public string Status { get; private set; } = "not initialized";

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_patch is not null) return Task.CompletedTask;

        Type? type = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(ScavSpawnsType, false);
            if (type is not null) break;
        }
        if (type is null)
        {
            Status = "BPS not installed";
            return Task.CompletedTask;
        }

        var method = type.GetMethod("GetNonMarksmanSpawnZones",
            BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(string)], null);
        if (method is null || method.ReturnType != typeof(List<string>))
        {
            Status = "unsupported BPS scav-zone API";
            logger.Warning("Interchange: BPS scav-zone API was not recognized; BPS remains enabled without the map-zone adapter.");
            return Task.CompletedTask;
        }

        _active = this;
        try
        {
            _patch = new ScavZonePatch(method);
            _patch.Enable();
            Status = "native map-zone adapter enabled";
            logger.Info("Interchange: BPS remains enabled; starter scavs use the reworked map's public Bot spawn zones. BPS wave, boss and PMC settings are preserved.");
        }
        catch
        {
            _patch = null;
            if (ReferenceEquals(_active, this)) _active = null;
            throw;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// OpenZones excludes dedicated boss areas. Requiring Savage/Bot spawn
    /// points also excludes player-only, PMC-only and boss-only coordinates.
    /// Preserve authored zone order and give BPS a fresh mutable list per call.
    /// </summary>
    public static List<string> GetEligibleScavZones(LocationBase map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var available = new HashSet<string>(StringComparer.Ordinal);
        if (map.SpawnPointParams is not null)
        {
            foreach (var point in map.SpawnPointParams)
            {
                if (string.IsNullOrWhiteSpace(point.BotZoneName) ||
                    !Contains(point.Categories, "Bot") || !Contains(point.Sides, "Savage")) continue;
                available.Add(point.BotZoneName);
            }
        }
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in (map.OpenZones ?? "").Split(','))
        {
            var zone = entry.Trim();
            if (available.Contains(zone) && seen.Add(zone)) result.Add(zone);
        }
        return result;
    }

    private static bool Contains(IEnumerable<string>? values, string expected)
    {
        if (values is null) return false;
        foreach (var value in values)
            if (string.Equals(value, expected, StringComparison.Ordinal)) return true;
        return false;
    }

    public void Dispose()
    {
        _patch?.Disable();
        _patch = null;
        if (ReferenceEquals(_active, this)) _active = null;
        Status = "disposed";
    }

    private sealed class ScavZonePatch(MethodBase target) : AbstractPatch(ModIdentity.Guid + ".bps.scavzones")
    {
        protected override MethodBase GetTargetMethod() => target;

        [PatchPrefix]
        private static bool Prefix(string location, ref List<string>? __result)
        {
            var owner = _active;
            if (owner is null || !InterchangeServerState.Ready ||
                !string.Equals(location, "interchange", StringComparison.OrdinalIgnoreCase)) return true;

            var map = owner._locations.Interchange?.Base;
            if (map is null || !string.Equals(map.Id, "interchange", StringComparison.OrdinalIgnoreCase)) return true;
            var zones = GetEligibleScavZones(map);
            // Empty means an incompatible/disabled map layout. Never return an
            // empty list that BPS interprets as permission to spawn anywhere.
            if (zones.Count == 0) return true;
            __result = zones;
            return false;
        }
    }
}
