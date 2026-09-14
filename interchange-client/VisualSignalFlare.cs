using System;
using EFT.PrefabSettings;
using Systems.Effects;
using UnityEngine;

namespace Manimal.Interchange.Client;

// Response flares are visual scene effects. They never register a player shot,
// acquire an inventory item, or apply the damaging cartridge's collision logic.
internal sealed class VisualSignalFlare : MonoBehaviour
{
    private FlareCartridgeSettings _settings = null!;
    private Rigidbody _body = null!;
    private GameObject _projectile = null!;
    private GameObject? _effect;
    private float _startedAt;
    private bool _ignited, _stopping, _initialized;

    internal void Initialize(FlareCartridgeSettings settings)
    {
        if (_initialized) throw new InvalidOperationException("Response flare already initialized");
        if (!settings || !settings.ProjectileAmmo || settings.Weight <= 0 || settings.FlareLifetime <= 0)
            throw new InvalidOperationException("Response flare settings are incomplete");
        _settings = settings;
        _startedAt = Time.time;
        var layer = LayerMask.NameToLayer("Triggers");
        if (layer < 0) throw new InvalidOperationException("Target Triggers layer is unavailable");
        gameObject.layer = layer;
        _body = GetComponent<Rigidbody>();
        if (!_body) _body = gameObject.AddComponent<Rigidbody>();
        PhysicsExtensions.UpdateController.SupportRigidbody(_body, 1, null);
        _body.isKinematic = false;
        _body.mass = settings.Weight;
        _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        _body.drag = 0;
        _projectile = Instantiate(settings.ProjectileAmmo, transform, false);
        _projectile.transform.forward = transform.forward;
        settings.SwitchMeshRenderersActive(false);
        if (settings.FlareEffectPrefab)
        {
            _effect = Instantiate(settings.FlareEffectPrefab, transform);
            if (_effect.TryGetComponent<FlareShotEffectSelector>(out var selector))
                selector.SetFlareEffect(settings.FlareColorType, settings.FlareLifetime);
            _effect.SetActive(false);
        }
        _initialized = true;
        enabled = true;
    }

    internal void Launch()
    {
        if (!_initialized) throw new InvalidOperationException("Response flare must be initialized before launch");
        _body.velocity = transform.forward * _settings.InitialSpeed;
    }

    private void Update()
    {
        if (!_initialized) return;
        if (!_ignited && !_stopping && Time.time >= _startedAt + _settings.FlareTimeAfterStart)
        {
            if (_body) _body.drag = _settings.RigidbodyDrag;
            if (_projectile) _projectile.SetActive(false);
            if (_effect) _effect!.SetActive(true);
            _ignited = true;
        }
        if (_ignited && Time.time + 3 >= _startedAt + _settings.FlareLifetime)
        { _ignited = false; _stopping = true; }
        if (_stopping && Time.time >= _startedAt + _settings.FlareLifetime) Destroy(gameObject);
    }
}
