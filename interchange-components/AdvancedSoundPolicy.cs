using System;
using UnityEngine;

namespace Manimal.Interchange.Components;

public sealed class AdvancedSoundPolicy : MonoBehaviour
{
    public string SourceKey = "";
    public MonoBehaviour Handler = null!;
    public AnimationCurve RolloffCurve = new();
    public bool EnableHighPassFilter;
    public static event Action<AdvancedSoundPolicy>? Destroyed;
    private void OnDestroy() => Destroyed?.Invoke(this);
}
