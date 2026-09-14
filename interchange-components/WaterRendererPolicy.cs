using UnityEngine;

namespace Manimal.Interchange.Components;

/// <summary>
/// Authoring-only evidence for a retail WaterRenderer projected to the native
/// SPT WaterRendererv3 component. The target renderer owns all render state;
/// this component only carries the source identity and the fields that have no
/// equivalent target serialization.
/// </summary>
public sealed class WaterRendererPolicy : MonoBehaviour
{
    public string SourceKey = "";
    public MonoBehaviour Owner = null!;
    public int SourceResolution;
    public bool UnderwaterEnabled;
    public int BlurIterations;
    public bool DynamicFoamEnabled;
    public float WaterLevel;
    public string SourceWaterPropertiesSha256 = "";
}
