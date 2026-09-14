using System;
using UnityEngine;

namespace Manimal.Interchange.Components;

[DefaultExecutionOrder(-31000)]
public sealed class AudioCatalog : MonoBehaviour
{
    public string SourceKey = "";
    public ScriptableObject[] Banks = Array.Empty<ScriptableObject>();
    public bool LoadOnEnable;
    public bool UnloadOnDisable;
    public ScriptableObject[] LimitedBanks = Array.Empty<ScriptableObject>();
    public bool[] LimitIfVisible = Array.Empty<bool>();
    public Vector2[] LimitRadius = Array.Empty<Vector2>();
    public float[] LimitPercentOfClip = Array.Empty<float>();
    public ScriptableObject[] BlendProfiles = Array.Empty<ScriptableObject>();
    public int[] FadeTypes = Array.Empty<int>();

    public static event Action<AudioCatalog>? Created;
    public static event Action<AudioCatalog>? Enabled;
    public static event Action<AudioCatalog>? Disabled;
    public static event Action<AudioCatalog>? Destroyed;
    private void Awake() => Created?.Invoke(this);
    private void OnEnable() => Enabled?.Invoke(this);
    private void OnDisable() => Disabled?.Invoke(this);
    private void OnDestroy() => Destroyed?.Invoke(this);

    public static float EaseInOut(float t) => t < .5f ? 2 * t * t : 1 - Mathf.Pow(-2 * t + 2, 2) / 2;

    public static bool IsChoked(Vector3 center, Vector2 radii, float rolloff, Vector3 position, Vector3 listener, float sqrDistance)
    {
        var radius = Mathf.Lerp(radii.x, radii.y, Mathf.InverseLerp(25, rolloff * rolloff, sqrDistance));
        var radiusSquared = radius * radius;
        if (!((center - position).sqrMagnitude < radiusSquared)) return false;
        var offset = radius - .2f;
        if (offset <= 0) return true;
        var away = center - listener;
        if (Vector3.Dot(center - position, away) <= 0) return true;
        if (away.sqrMagnitude <= offset * offset) return (listener - position).sqrMagnitude < radiusSquared;
        return (center + away.normalized * offset - position).sqrMagnitude < radiusSquared;
    }
}
