using System;
using UnityEngine;

namespace Manimal.Interchange.Components;

/// <summary>
/// Serialized source authoring retained beside the target ambient audio
/// components.  SPT 4.1.5 has an <c>AmbientSoundBlender</c> and an
/// <c>EnvironmentSoundBlendSystem</c>, but their schemas removed the source
/// directional fade fields and the portal scoring controls.  The converter
/// writes this marker on the source component's GameObject and the client
/// binding consumes the values while using the target AudioMixer/fader APIs.
/// </summary>
[DefaultExecutionOrder(-31000)]
[DisallowMultipleComponent]
public sealed class AmbientBlendPolicy : MonoBehaviour
{
    public string SourceKey = "";
    public bool IsEnvironmentSystem;

    // AmbientSoundBlender authoring.  These fields intentionally use the
    // target-independent Unity wire types so the component assembly does not
    // reference the target game's Assembly-CSharp.
    public int FadeInType;
    public int FadeOutType;
    public float FadeInDuration;
    public float FadeOutDuration;
    public float FromOutToInTransitionTime;
    public float FromInToOutTransitionTime;
    public UnityEngine.Object OutdoorMixerParams = null!;
    public UnityEngine.Object IndoorMixerParams = null!;
    public AnimationCurve VolumeCurve = new AnimationCurve();
    public AnimationCurve OcclusionCurve = new AnimationCurve();
    public float BlendSpeed;
    public float NoPortalBlendSpeed;
    public float BlendDurationMult;
    public float MaxHighPassFreq;
    public float MinHighPassFreq;
    public float MinLowpassFreq;
    public float MaxLowpassFreq;
    public float MaxOutdoorReverbSendDb;
    public float MaxOutdoorDelaySendDb;

    // EnvironmentSoundBlendSystem authoring.  AmbientSoundBlenders are
    // target components and therefore remain ordinary Unity object PPtrs.
    public UnityEngine.Object[] AmbientSoundBlenders = Array.Empty<UnityEngine.Object>();
    public float ListenerHeightWeight;
    public float DepthCalculationMult;
    public float ConnectedRoomPenalty;
    public float NeighborRoomPenalty;
    public bool IgnoreDistanceRatio;

    public static event Action<AmbientBlendPolicy>? Created;
    public static event Action<AmbientBlendPolicy>? Enabled;
    public static event Action<AmbientBlendPolicy>? Disabled;
    public static event Action<AmbientBlendPolicy>? Destroyed;

    private void Awake() => Created?.Invoke(this);
    private void OnEnable() => Enabled?.Invoke(this);
    private void OnDisable() => Disabled?.Invoke(this);
    private void OnDestroy() => Destroyed?.Invoke(this);
}
