using UnityEngine;
using UnityEngine.Rendering;

namespace Manimal.Interchange.Components;

[RequireComponent(typeof(Light))]
public sealed class SunShadowMap : MonoBehaviour
{
    public string SourceKey = "";
    public static readonly int TextureProperty = Shader.PropertyToID("_SunCascadedShadowMap");
    private Light? _light;
    private CommandBuffer? _commands;
    private Texture? _previousTexture;

    private void OnEnable()
    {
        _light = GetComponent<Light>();
        if (!_light) return;
        _previousTexture = Shader.GetGlobalTexture(TextureProperty);
        _commands = new CommandBuffer { name = "Manimal Interchange sun shadow map" };
        _commands.SetGlobalTexture(TextureProperty, BuiltinRenderTextureType.CurrentActive, RenderTextureSubElement.Default);
        _light.AddCommandBuffer(LightEvent.AfterShadowMap, _commands);
    }

    private void OnDisable()
    {
        if (_commands == null) return;
        if (_light) _light!.RemoveCommandBuffer(LightEvent.AfterShadowMap, _commands);
        _commands.Release();
        _commands = null;
        Shader.SetGlobalTexture(TextureProperty, _previousTexture);
        _previousTexture = null;
    }
}
