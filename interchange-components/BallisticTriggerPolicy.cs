using UnityEngine;

namespace Manimal.Interchange.Components;

[DisallowMultipleComponent]
public sealed class BallisticTriggerPolicy : MonoBehaviour
{
    public string SourceKey = "";
    public MonoBehaviour Trigger = null!;
    public bool CanBreakByKnife;
    public float DamageThreshold;

    public bool Allows(bool melee, float damage)
    {
        // The retail COMISS/JA sequence rejects only damage below threshold;
        // retaining that comparison also preserves its unordered-float result.
        return (!melee || CanBreakByKnife) && !(damage < DamageThreshold);
    }
}
