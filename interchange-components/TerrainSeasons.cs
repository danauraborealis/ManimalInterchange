using System;
using UnityEngine;

namespace Manimal.Interchange.Components;

[Serializable]
public sealed class TerrainSeasonSlot
{
    public int Season;
    public int Quality;
    public Material Material = null!;
    public ScriptableObject Keywords = null!;
    public ScriptableObject Properties = null!;
}

[DisallowMultipleComponent]
public sealed class TerrainSeasons : MonoBehaviour
{
    public string SourceKey = "";
    public MonoBehaviour Terrain = null!;
    public TerrainSeasonSlot[] Slots = Array.Empty<TerrainSeasonSlot>();

    public TerrainSeasonSlot Get(int season, int quality)
    {
        if (season < 0 || season > 5 || quality < 0 || quality > 2 || Slots.Length != 18)
            throw new InvalidOperationException("Incomplete terrain season/quality palette: " + SourceKey);
        // The converter writes a dense array and verifies all 18 identities.
        var slot = Slots[season * 3 + quality];
        if (slot.Season != season || slot.Quality != quality || !slot.Material || !slot.Keywords || !slot.Properties)
            throw new InvalidOperationException("Invalid terrain seasonal slot: " + SourceKey);
        return slot;
    }
}
