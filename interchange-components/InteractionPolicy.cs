using UnityEngine;

namespace Manimal.Interchange.Components;

[DisallowMultipleComponent]
public sealed class InteractionPolicy : MonoBehaviour
{
    public string SourceKey = "";
    public Component Owner = null!;
    public Component RelatedTrigger = null!;
    public bool LockOperationsAfterOpen;
    public bool LogInteractions;
    public string InteractionLogLabel = "";
}
