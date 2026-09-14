using System.Collections.Generic;
using UnityEngine;

namespace Manimal.Interchange.Components;

public sealed class MountainViewAngle : MonoBehaviour
{
    public string SourceKey = "";
    public float DirectionAngle;
    public float CullAngle;
    public float FeatherAngle;
    public const string Keyword = "_EFFECT_ENABLED";
    public static readonly int DirectionProperty = Shader.PropertyToID("_DirectionLocalXZ");
    public static readonly int CullProperty = Shader.PropertyToID("_SkyboxMountainsCosCull");
    public static readonly int FeatherProperty = Shader.PropertyToID("_SkyboxMountainsCosFeather");
    private static readonly List<MountainViewAngle> Active = new();
    private static bool _previousKeyword;
    private static float _previousCull, _previousFeather;
    private Renderer[] _renderers = System.Array.Empty<Renderer>();
    private Vector4[] _previousDirections = System.Array.Empty<Vector4>();
    private MaterialPropertyBlock? _block;

    public static Vector2 LocalDirection(Transform target, float angle)
    {
        var radians = angle * Mathf.Deg2Rad;
        var direction = target.InverseTransformDirection(new Vector3(Mathf.Cos(radians), 0, Mathf.Sin(radians)));
        var local = new Vector2(direction.x, direction.z);
        var length = local.magnitude;
        return length > 0.000001f ? local / length : Vector2.right;
    }

    private void OnEnable()
    {
        _renderers = GetComponentsInChildren<Renderer>(true);
        if (_renderers.Length == 0) { enabled = false; return; }
        _block ??= new MaterialPropertyBlock();
        _previousDirections = new Vector4[_renderers.Length];
        for (var i = 0; i < _renderers.Length; i++)
        {
            _renderers[i].GetPropertyBlock(_block);
            _previousDirections[i] = _block.GetVector(DirectionProperty);
        }
        if (Active.Count == 0)
        {
            _previousKeyword = Shader.IsKeywordEnabled(Keyword);
            _previousCull = Shader.GetGlobalFloat(CullProperty);
            _previousFeather = Shader.GetGlobalFloat(FeatherProperty);
        }
        Active.Add(this);
        Shader.EnableKeyword(Keyword);
        Refresh();
    }

    public void Refresh()
    {
        if (!isActiveAndEnabled || _renderers.Length == 0) return;
        var half = CullAngle * 0.5f * Mathf.Deg2Rad;
        Shader.SetGlobalFloat(CullProperty, Mathf.Cos(half));
        // The pinned retail implementation clamps at 179.82 degrees, not PI.
        Shader.SetGlobalFloat(FeatherProperty, Mathf.Cos(Mathf.Clamp(half + FeatherAngle * Mathf.Deg2Rad, 0, 3.138451099395752f)));
        for (var i = 0; i < _renderers.Length; i++)
        {
            var renderer = _renderers[i];
            if (!renderer) continue;
            var direction = LocalDirection(renderer.transform, DirectionAngle);
            renderer.GetPropertyBlock(_block);
            _block!.SetVector(DirectionProperty, new Vector4(direction.x, direction.y, 0, 0));
            renderer.SetPropertyBlock(_block);
        }
    }

    private void OnDisable()
    {
        if (!Active.Remove(this)) return;
        for (var i = 0; i < _renderers.Length; i++)
        {
            if (!_renderers[i]) continue;
            _renderers[i].GetPropertyBlock(_block);
            _block!.SetVector(DirectionProperty, _previousDirections[i]);
            _renderers[i].SetPropertyBlock(_block);
        }
        if (Active.Count != 0) { Active[Active.Count - 1].Refresh(); return; }
        if (_previousKeyword) Shader.EnableKeyword(Keyword); else Shader.DisableKeyword(Keyword);
        Shader.SetGlobalFloat(CullProperty, _previousCull);
        Shader.SetGlobalFloat(FeatherProperty, _previousFeather);
    }
}
