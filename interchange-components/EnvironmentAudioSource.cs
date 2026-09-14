using System;
using UnityEngine;

namespace Manimal.Interchange.Components;

/// <summary>
/// Authoring data retained for a source HandlerEnvironmentAudioSource.
///
/// The native handler is absent from the target assembly.  The scene
/// projector replaces that component at its original path ID with this
/// marker, retaining the authored mixer PPtrs so the client binding can
/// recreate the native initialization path.
/// </summary>
[DefaultExecutionOrder(-31000)]
[DisallowMultipleComponent]
public sealed class EnvironmentAudioSource : MonoBehaviour
{
    public string SourceKey = "";
    public UnityEngine.Object IndoorMixerGroup = null!;
    public UnityEngine.Object OutdoorMixerGroup = null!;
    public bool UseIndoorOcclusion = true;

    public static event Action<EnvironmentAudioSource>? Created;
    public static event Action<EnvironmentAudioSource>? Enabled;
    public static event Action<EnvironmentAudioSource>? Disabled;
    public static event Action<EnvironmentAudioSource>? Destroyed;

    private void Awake() => Created?.Invoke(this);
    private void OnEnable() => Enabled?.Invoke(this);
    private void OnDisable() => Disabled?.Invoke(this);
    private void OnDestroy() => Destroyed?.Invoke(this);
}
