using System;
using UnityEngine;

namespace Manimal.Interchange.Components;

public sealed class SceneSignalFlareLauncher : MonoBehaviour
{
    public string SourceKey = "";
    public GameObject FlarePrefab = null!;
    public GameObject SoundPrefab = null!;
    public Vector3 Direction;
    public float ShotDelay;
    public static event Action<SceneSignalFlareLauncher, Vector3>? Requested;
    public static event Action<SceneSignalFlareLauncher>? Destroyed;
    public void Launch() => Launch(transform.position);
    public void Launch(Vector3 position)
    {
        if (Requested == null) throw new InvalidOperationException("Interchange flare runtime binding is unavailable");
        Requested(this, position);
    }
    private void OnDestroy() => Destroyed?.Invoke(this);
}
