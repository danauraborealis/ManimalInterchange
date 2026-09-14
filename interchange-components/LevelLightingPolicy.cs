using System;
using UnityEngine;

namespace Manimal.Interchange.Components;

/// <summary>
/// Authored lighting values that were present on the retail LevelSettings
/// component but have no serialized fields in the target game version.
/// </summary>
[DefaultExecutionOrder(-32000)]
[DisallowMultipleComponent]
public sealed class LevelLightingPolicy : MonoBehaviour
{
    public string SourceKey = "";
    public Component NativeLevelSettings = null!;
    public float DistantShadowOffset;
    public float DistantShadowFarPlane;
    public Color MinSmokeAmbientColor;
    public bool UseNativeCameraPrefab;

    public static event Action<LevelLightingPolicy>? Enabled;
    public static event Action<LevelLightingPolicy>? Disabled;
    public static event Action<LevelLightingPolicy>? Destroyed;

    private void OnEnable() => Enabled?.Invoke(this);
    private void OnDisable() => Disabled?.Invoke(this);
    private void OnDestroy() => Destroyed?.Invoke(this);
}
