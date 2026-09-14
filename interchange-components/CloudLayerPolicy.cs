using UnityEngine;

namespace Manimal.Interchange.Components;

public sealed class CloudLayerPolicy : MonoBehaviour
{
    public string SourceKey = "";
    public MonoBehaviour Controller = null!;
    public Shader PixelShader = null!;
    public float StretchDistance;
}
