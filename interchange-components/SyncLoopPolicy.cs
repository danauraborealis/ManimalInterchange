using System;
using UnityEngine;

namespace Manimal.Interchange.Components;

[DisallowMultipleComponent]
public sealed class SyncLoopPolicy : MonoBehaviour
{
    public string SourceKey = "";
    public MonoBehaviour Owner = null!;
    public float ScheduleAheadTime = .1f;
    public int MaxCatchUpPerFrame = 4;
    public event Action<bool>? StateChanged;
    public static event Action<SyncLoopPolicy, bool>? StateRequested;
    public static event Action<SyncLoopPolicy>? Destroyed;
    public void ApplyState(bool state) => StateRequested?.Invoke(this, state);
    public void NotifyState(bool state) => StateChanged?.Invoke(state);
    private void OnDestroy() => Destroyed?.Invoke(this);
}
