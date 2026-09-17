using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Utils;
using Path = System.IO.Path;

namespace Manimal.MapBackport;

/// <summary>Retains reworked positions and item pools while using SPT's raid loot budget.</summary>
public static class LegacyLootCompatibility
{
    public static LooseLoot ReadBaseline(JsonUtil json, string map)
    {
        if (map is not ("interchange" or "lighthouse")) throw new ArgumentOutOfRangeException(nameof(map));
        var path = Path.Combine(AppContext.BaseDirectory, "SPT_Data", "database", "locations", map, "looseLoot.json");
        return json.Deserialize<LooseLoot>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Missing SPT loose-loot baseline for " + map);
    }

    public static LooseLoot Reconcile(LooseLoot replacement, LooseLoot baseline,
        Dictionary<MongoId, TemplateItem> templates, JsonUtil json)
    {
        var count = baseline.SpawnpointCount ?? throw new InvalidDataException("SPT loose-loot count is missing.");
        if (!double.IsFinite(count.Mean) || !double.IsFinite(count.Std) || count.Mean < 0 || count.Std < 0)
            throw new InvalidDataException("SPT loose-loot count is invalid.");
        replacement.SpawnpointCount = count with { };

        // A point's probability is a relative draw weight in SPT, not a separate
        // per-raid Bernoulli chance. Keep the rework's location/item weighting;
        // correcting the draw count fixes its 5–6x imported loot budget.
        var present = new HashSet<MongoId>();
        void Collect(IEnumerable<Spawnpoint>? points)
        {
            if (points is null) return;
            foreach (var point in points)
                if (point.Template?.Items is not null)
                    foreach (var item in point.Template.Items) present.Add(item.Template);
        }
        Collect(replacement.SpawnpointsForced);
        Collect(replacement.Spawnpoints);

        var missingQuestTypes = new HashSet<MongoId>();
        foreach (var point in baseline.SpawnpointsForced ?? [])
            if (point.Template?.Items is not null)
                foreach (var item in point.Template.Items)
                    if (!present.Contains(item.Template) && templates.TryGetValue(item.Template, out var template)
                        && template.Properties?.QuestItem == true) missingQuestTypes.Add(item.Template);

        var forced = new List<Spawnpoint>(replacement.SpawnpointsForced ?? []);
        foreach (var point in baseline.SpawnpointsForced ?? [])
        {
            var restore = false;
            if (point.Template?.Items is not null)
                foreach (var item in point.Template.Items)
                    if (missingQuestTypes.Contains(item.Template)) { restore = true; break; }
            if (!restore) continue;
            // Include every authored variant of an absent quest item, with its
            // original grouping/chance. No references to the baseline escape.
            forced.Add(json.Deserialize<Spawnpoint>(json.Serialize(point)!)
                ?? throw new InvalidDataException("Could not copy a legacy quest spawn."));
        }
        replacement.SpawnpointsForced = forced;
        return replacement;
    }
}
