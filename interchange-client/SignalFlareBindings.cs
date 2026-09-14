using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using EFT;
using EFT.Airdrop;
using EFT.PrefabSettings;
using Manimal.Interchange.Components;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZLinq;

namespace Manimal.Interchange.Client;

internal static class SignalFlareBindings
{
    private static readonly Dictionary<SceneSignalFlareLauncher, CancellationTokenSource> Lifetimes = new();
    internal static int Pending { get; private set; }
    internal static void Enable()
    {
        SceneSignalFlareLauncher.Requested += Request;
        SceneSignalFlareLauncher.Destroyed += Remove;
    }
    internal static void Disable()
    {
        SceneSignalFlareLauncher.Requested -= Request;
        SceneSignalFlareLauncher.Destroyed -= Remove;
        foreach (var launcher in Lifetimes.Keys.AsValueEnumerable().ToArray()) Remove(launcher);
    }
    private static void Remove(SceneSignalFlareLauncher launcher)
    {
        if (!Lifetimes.TryGetValue(launcher, out var lifetime)) return;
        Lifetimes.Remove(launcher); lifetime.Cancel(); lifetime.Dispose();
    }
    private static void Request(SceneSignalFlareLauncher launcher, Vector3 position)
    {
        if (!launcher || !launcher.FlarePrefab || launcher.Direction.sqrMagnitude < 0.000001f ||
            launcher.ShotDelay < 0 || float.IsInfinity(launcher.ShotDelay) || float.IsNaN(launcher.ShotDelay))
            throw new InvalidOperationException("Invalid authored response flare settings");
        if (!Lifetimes.TryGetValue(launcher, out var lifetime))
        { lifetime = new CancellationTokenSource(); Lifetimes.Add(launcher, lifetime); }
        Launch(launcher, position, lifetime.Token).Forget(error => Plugin.Log.LogError(error));
    }
    private static async UniTask Launch(SceneSignalFlareLauncher launcher, Vector3 position, CancellationToken token)
    {
        Pending++;
        GameObject? flare = null;
        try
        {
            // Source Task.Delay uses elapsed time, independent of game time scale.
            await UniTask.Delay(checked((int)(launcher.ShotDelay * 1000)), ignoreTimeScale: true, cancellationToken: token);
            token.ThrowIfCancellationRequested();
            if (!launcher) return;
            if (!launcher.FlarePrefab.TryGetComponent<FlareCartridgeSettings>(out _))
                throw new InvalidOperationException("Response flare prefab has no native settings");
            flare = UnityEngine.Object.Instantiate(launcher.FlarePrefab, position, Quaternion.LookRotation(launcher.Direction));
            SceneManager.MoveGameObjectToScene(flare, launcher.gameObject.scene);
            flare.SetActive(true);
            if (flare.TryGetComponent<FlareCartridge>(out var damagingCartridge))
            { damagingCartridge.enabled = false; UnityEngine.Object.Destroy(damagingCartridge); }
            var visual = flare.AddComponent<VisualSignalFlare>();
            // Use the cloned settings so hiding the case mesh cannot modify the
            // source prefab or another launched response flare.
            var settings = flare.GetComponent<FlareCartridgeSettings>();
            if (launcher.SoundPrefab)
            {
                var sound = UnityEngine.Object.Instantiate(launcher.SoundPrefab, flare.transform);
                if (sound.TryGetComponent<FlarePatronSound>(out var patron)) patron.Init(settings.FlareLifetime);
            }
            visual.Initialize(settings); visual.Launch();
            flare = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (flare) UnityEngine.Object.Destroy(flare);
            Pending--;
        }
    }
}
