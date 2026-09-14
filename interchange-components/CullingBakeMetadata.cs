using System;
using UnityEngine;

namespace Manimal.Interchange.Components;

// Retail bake inputs are retained alongside the compatible target culling
// component. Runtime visibility comes from the preserved native bake sidecars.
public sealed class CullingBakeMetadata : MonoBehaviour
{
    public string SourceKey = "";
    public Component Owner = null!;
    public string VolumeGuid = "";
    public Material[] ForceDoubleSidedMaterials = Array.Empty<Material>();
}
