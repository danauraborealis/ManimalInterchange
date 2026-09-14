using System;
using UnityEngine;

namespace Manimal.Interchange.Components;

public sealed class FlareExitPolicy : MonoBehaviour
{
    public string SourceKey = "";
    public Component Collector = null!;
    public SceneSignalFlareLauncher Launcher = null!;
    public static event Action<FlareExitPolicy>? Destroyed;
    private void OnDestroy() => Destroyed?.Invoke(this);
}
