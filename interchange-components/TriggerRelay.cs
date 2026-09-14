using System;
using UnityEngine;

namespace Manimal.Interchange.Components;

public enum TriggerRelayKind { RandomChance, ItemRequirement, PlayerNotification }

public sealed class TriggerRelay : MonoBehaviour
{
    public string SourceKey = "";
    public TriggerRelayKind Kind;
    public string Input = "";
    public string Success = "";
    public string Failure = "";
    public float Chance;
    public string[] RequiredItemTemplates = Array.Empty<string>();
    public string Message = "";
    public bool NotificationForAll;
    public static event Action<TriggerRelay>? Started;
    public static event Action<TriggerRelay>? Destroyed;
    private void Start() => Started?.Invoke(this);
    private void OnDestroy() => Destroyed?.Invoke(this);

    public string SelectRandomOutcome(float roll) => roll <= Chance ? Success : Failure;
}
