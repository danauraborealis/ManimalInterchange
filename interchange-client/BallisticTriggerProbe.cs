using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using EFT;
using EFT.Ballistics;
using EFT.GameTriggers;
using HarmonyLib;
using Manimal.Interchange.Components;
using Manimal.Interchange.Shared;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZLinq;

namespace Manimal.Interchange.Client;

internal static class BallisticTriggerProbe
{
    private sealed class Input { public string Bundle = ""; public string Sha256 = ""; public string Scene = ""; }
    internal static async UniTask<object> Run(CancellationToken token)
    {
        var input = JsonConvert.DeserializeObject<Input>(File.ReadAllText(Path.Combine(Plugin.Root, "ballistic-validation.json")))
            ?? throw new InvalidDataException("Ballistic fixture manifest missing");
        var path = ManifestRules.Resolve(Plugin.Root, input.Bundle);
        if (ManifestRules.Hash(path) != input.Sha256) throw new InvalidDataException("Ballistic fixture changed");
        var beforeScenes = SceneManager.sceneCount;
        var explosionField = AccessTools.Field(typeof(ExplosionSharedMethods), "_onExplosion");
        int Callbacks() => (explosionField.GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;
        var beforeCallbacks = Callbacks();
        var checks = new List<string>();
        var errors = new List<string>();
        void Check(bool pass, string name) { if (!pass) throw new InvalidDataException(name); checks.Add(name); }
        void Error(string message, string stack, LogType kind)
        { if (kind is LogType.Error or LogType.Exception or LogType.Assert) errors.Add(message); }
        var done = AccessTools.Field(typeof(TriggerBallistic), "_isDone");
        var target = AccessTools.Field(typeof(TriggerBallistic), "_targetCollider");
        AssetBundle? bundle = null;
        Scene scene = default;
        TriggerBallistic[] triggers = Array.Empty<TriggerBallistic>();
        Application.logMessageReceived += Error;
        try
        {
            token.ThrowIfCancellationRequested();
            var request = AssetBundle.LoadFromFileAsync(path);
            await UniTask.WaitUntil(() => request.isDone);
            bundle = request.assetBundle;
            if (!bundle) throw new InvalidDataException("Ballistic fixture failed to load");
            Check(bundle.GetAllScenePaths().AsValueEnumerable().SequenceEqual(new[] { input.Scene }), "Exact ballistic fixture scene");
            var load = SceneManager.LoadSceneAsync(input.Scene, LoadSceneMode.Additive);
            await UniTask.WaitUntil(() => load.isDone);
            scene = SceneManager.GetSceneByPath(input.Scene);
            var roots = scene.GetRootGameObjects();
            Check(roots.Length == 1 && !roots[0].activeSelf, "Fixture remains inactive until configured");
            var root = roots[0];
            triggers = root.GetComponentsInChildren<TriggerBallistic>(true);
            Check(triggers.Length == 3, "Three exact target trigger components deserialized");
            var policies = root.GetComponentsInChildren<BallisticTriggerPolicy>(true);
            Check(policies.Length == 2, "Both ballistic policy components deserialized");
            var owned = policies.AsValueEnumerable().Single(p => !p.CanBreakByKnife);
            var knife = policies.AsValueEnumerable().Single(p => p.CanBreakByKnife);
            var control = triggers.AsValueEnumerable().Single(t => !t.GetComponent<BallisticTriggerPolicy>());
            Check(owned.DamageThreshold == 10 && knife.DamageThreshold == 10, "Exact source damage threshold survives serialization");
            var index = 0;
            var highPolyMask = LayersMaskController.HighPolyWithTerrainMask.value;
            var layer = 0;
            while (layer < 32 && (highPolyMask & (1 << layer)) == 0) layer++;
            Check(layer < 32, "Native explosion collision mask available");
            foreach (var trigger in triggers)
            {
                trigger.transform.localPosition = new Vector3(index++ * 10, 0, 0);
                trigger.gameObject.layer = layer;
            }
            root.SetActive(true);
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
            Check(Callbacks() == beforeCallbacks + 3, "Native Start registers explosion callbacks");
            var hitField = AccessTools.Field(typeof(BallisticCollider), "_onHitAction");
            foreach (var trigger in triggers)
                Check(((Delegate?)hitField.GetValue(target.GetValue(trigger)))?.GetInvocationList().Length == 1, "Native Start registers one collider hit handler: " + trigger.name);
            void Hit(TriggerBallistic trigger, EDamageType type, float damage, bool expected, string name)
            {
                token.ThrowIfCancellationRequested();
                done.SetValue(trigger, false);
                var collider = (BallisticCollider)target.GetValue(trigger);
                collider.ApplyHit(new DamageInfo { DamageType = type, Damage = damage }, default);
                Check((bool)done.GetValue(trigger) == expected, name);
            }
            var guarded = (TriggerBallistic)owned.Trigger;
            Hit(control, EDamageType.Melee, 1, true, "Unowned SPT trigger keeps native hit behavior");
            Hit(guarded, EDamageType.Bullet, 9.999f, false, "Subthreshold bullet rejected");
            Hit(guarded, EDamageType.Bullet, 10, true, "Threshold boundary bullet accepted");
            Hit(guarded, EDamageType.Bullet, 10.001f, true, "Above-threshold bullet accepted");
            Hit(guarded, EDamageType.Bullet, 0, false, "Zero-damage bullet rejected");
            Hit(guarded, EDamageType.Melee, 100, false, "Knife cannot trigger an authored knife restriction");
            Hit(guarded, EDamageType.Bullet, float.NaN, true, "Unordered damage matches retail COMISS behavior");
            Hit((TriggerBallistic)knife.Trigger, EDamageType.Melee, 10, true, "Enabled knife policy accepts threshold damage");
            Hit((TriggerBallistic)knife.Trigger, EDamageType.Melee, 9, false, "Enabled knife policy still checks damage");
            owned.Trigger = control;
            try { Hit(guarded, EDamageType.Melee, 1, true, "Policy owner mismatch leaves native trigger untouched"); }
            finally { owned.Trigger = guarded; }
            Hit(guarded, EDamageType.Bullet, 10, true, "One-use trigger accepts its first valid hit");
            var guardedCollider = (BallisticCollider)target.GetValue(guarded);
            guardedCollider.ApplyHit(new DamageInfo { DamageType = EDamageType.Bullet, Damage = 10 }, default);
            Check((bool)done.GetValue(guarded) && !(bool)AccessTools.Method(typeof(TriggerBallistic), "CanProceed").Invoke(guarded, null), "Native one-use state still blocks subsequent events");
            done.SetValue(guarded, false);
            Physics.SyncTransforms();
            AccessTools.Method(typeof(TriggerBallistic), "OnExplosion").Invoke(guarded,
                new object[] { guarded.transform.position + Vector3.back * 3, 5f });
            Check((bool)done.GetValue(guarded), "Native explosion raycast bypasses only the bullet/knife damage filter");
        }
        finally
        {
            if (scene.IsValid() && scene.isLoaded)
            {
                var unload = SceneManager.UnloadSceneAsync(scene);
                await UniTask.WaitUntil(() => unload.isDone);
            }
            if (bundle != null) bundle.Unload(true);
            Application.logMessageReceived -= Error;
        }
        Check(Callbacks() == beforeCallbacks, "Native explosion callbacks removed on scene unload");
        Check(SceneManager.sceneCount == beforeScenes, "Original menu scene count restored");
        Check(!triggers.AsValueEnumerable().Any(t => t), "All native trigger objects destroyed");
        Check(errors.Count == 0, "No Unity errors: " + string.Join("; ", errors));
        return new { Passed = true, BundleSha256 = input.Sha256, CheckCount = checks.Count, Checks = checks, UnityErrors = errors,
            Scope = "Actual native collider.ApplyHit -> subscribed target handler -> one-use state; exact threshold/knife policies, unowned control, owner guard, native explosion collision path and natural subscription cleanup. Raid trigger effects require full-map tests." };
    }
}
