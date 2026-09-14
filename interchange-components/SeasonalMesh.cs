using System;
using UnityEngine;

namespace Manimal.Interchange.Components;

public sealed class SeasonalMesh : MonoBehaviour
{
    public string SourceKey = "";
    public MeshRenderer Renderer = null!;
    public Material[] Summer = Array.Empty<Material>();
    public Material[] Autumn = Array.Empty<Material>();
    public Material[] Winter = Array.Empty<Material>();
    public Material[] Spring = Array.Empty<Material>();
    public Material[] AutumnLate = Array.Empty<Material>();
    public Material[] SpringEarly = Array.Empty<Material>();

    public static event Action<SeasonalMesh>? Started;
    public static event Action<SeasonalMesh>? Destroyed;
    private void Start() => Started?.Invoke(this);
    private void OnDestroy() => Destroyed?.Invoke(this);

    public Material[] ForSeason(int season) => season switch
    {
        0 => Summer, 1 => Autumn, 2 => Winter, 3 => Spring,
        4 => AutumnLate, 5 => SpringEarly,
        _ => throw new ArgumentOutOfRangeException(nameof(season))
    };
}
