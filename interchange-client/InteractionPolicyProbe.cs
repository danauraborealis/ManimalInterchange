using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BepInEx.Logging;
using Cysharp.Threading.Tasks;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using Manimal.Interchange.Components;
using Manimal.Interchange.Shared;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZLinq;

namespace Manimal.Interchange.Client;

internal static class InteractionPolicyProbe
{
    private sealed class Input { public string Bundle = ""; public string Sha256 = ""; public string Scene = ""; }
    internal static async UniTask<object> Run(CancellationToken token)
    {
        var input = JsonConvert.DeserializeObject<Input>(File.ReadAllText(Path.Combine(Plugin.Root, "interaction-validation.json")))!;
        var path = ManifestRules.Resolve(Plugin.Root, input.Bundle);
        if (ManifestRules.Hash(path) != input.Sha256) throw new InvalidDataException("Interaction fixture changed");
        var before = SceneManager.sceneCount; var checks = new List<string>(); var errors = new List<string>(); var logs = new List<string>();
        void Check(bool pass, string name) { if (!pass) throw new InvalidDataException(name); checks.Add(name); }
        void Error(string message, string stack, LogType kind) { if (kind is LogType.Error or LogType.Exception or LogType.Assert) errors.Add(message + "\n" + stack); }
        void Log(object sender, LogEventArgs args) { if (args.Data?.ToString() is string message && message.StartsWith("Switch log: Interaction:")) logs.Add(message); }
        AssetBundle? bundle = null; Scene scene = default;
        Application.logMessageReceived += Error; Plugin.Log.LogEvent += Log;
        try
        {
            token.ThrowIfCancellationRequested();
            var request = AssetBundle.LoadFromFileAsync(path); await UniTask.WaitUntil(() => request.isDone);
            bundle = request.assetBundle; Check(bundle, "Bundle loaded");
            Check(bundle!.GetAllScenePaths().AsValueEnumerable().SequenceEqual(new[] { input.Scene }), "Exact fixture scene");
            var load = SceneManager.LoadSceneAsync(input.Scene, LoadSceneMode.Additive); await UniTask.WaitUntil(() => load.isDone);
            scene = SceneManager.GetSceneByPath(input.Scene); var root = scene.GetRootGameObjects().AsValueEnumerable().Single();
            Check(!root.activeSelf, "Fixture inactive before native initialization");
            var policies = root.GetComponentsInChildren<InteractionPolicy>(true);
            Check(policies.Length == 3 && policies.AsValueEnumerable().All(p => p.Owner && p.RelatedTrigger), "Serialized owner and trigger references restored");
            var ownedDoor = (Door)policies.AsValueEnumerable().Single(p => p.SourceKey == "probe/Door").Owner;
            var cardPolicy = policies.AsValueEnumerable().Single(p => p.SourceKey == "probe/KeycardDoor"); var card = (KeycardDoor)cardPolicy.Owner;
            var switchPolicy = policies.AsValueEnumerable().Single(p => p.SourceKey == "probe/Switch"); var ownedSwitch = (Switch)switchPolicy.Owner;
            var control = root.transform.Find("ControlKeycardDoor").GetComponent<KeycardDoor>();
            var controlDoor = root.transform.Find("ControlDoor").GetComponent<Door>();
            var controlSwitch = root.transform.Find("ControlSwitch").GetComponent<Switch>();
            Check(cardPolicy.LockOperationsAfterOpen && switchPolicy.LogInteractions && switchPolicy.InteractionLogLabel == "KORD-14", "Authored active policies survive bundle serialization");
            root.SetActive(true); await UniTask.Yield();
            foreach (var owner in new Door[] { ownedDoor, card })
            {
                var trigger = (PhysicsTriggerHandler)owner.GetComponent<InteractionPolicy>().RelatedTrigger;
                Check(!trigger.enabled && !trigger.trigger.enabled, "Native Awake disables owned linked handler and collider: " + owner.name);
            }
            foreach (var owner in new WorldInteractiveObject[] { controlDoor, control, controlSwitch, ownedSwitch })
            {
                var trigger = owner.GetComponentInChildren<PhysicsTriggerHandler>(true);
                Check(trigger.enabled && trigger.trigger.enabled, "Other trigger state retained: " + owner.name);
            }
            InteractionPolicyBindings.InitializeDoor(ownedDoor);
            Check(!((PhysicsTriggerHandler)ownedDoor.GetComponent<InteractionPolicy>().RelatedTrigger).enabled, "Repeated linked-trigger initialization is idempotent");
            var unlock = AccessTools.Method(typeof(KeycardDoor), "UnlockCoroutine");
            var openOnUnlock = AccessTools.Field(typeof(KeycardDoor), "_openOnUnlock");
            var native = (IEnumerator)unlock.Invoke(card, null);
            Check(native.MoveNext() && native.Current is IEnumerator && card.Operatable, "Native unlock yields its sound sequence before applying one-use policy");
            // The menu has no BetterAudio service. Preserve and inspect the native
            // nested sound routine, then advance the outer completion path only.
            Check(!native.MoveNext() && !card.Operatable, "Native unlock Open dispatch completes before disabling owned operations");
            var unowned = (IEnumerator)unlock.Invoke(control, null);
            Check(unowned.MoveNext() && unowned.Current is IEnumerator, "Unowned native unlock retains its nested sound routine");
            Check(!unowned.MoveNext() && control.Operatable, "Unowned keycard remains operatable after native unlock");
            card.Operatable = true; openOnUnlock.SetValue(card, false);
            var noOpen = (IEnumerator)unlock.Invoke(card, null);
            Check(noOpen.MoveNext() && !noOpen.MoveNext() && card.Operatable, "Unlock without automatic opening remains operatable");
            openOnUnlock.SetValue(card, true);
            var cancelled = (IEnumerator)unlock.Invoke(card, null);
            Check(cancelled.MoveNext(), "Cancellable unlock reached native yield");
            ((IDisposable)cancelled).Dispose(); Check(card.Operatable, "Disposing interrupted unlock does not consume operations");
            var result = new InteractionResult(EInteractionType.Lock);
            ownedSwitch.Interact(result); controlSwitch.Interact(result);
            Check(logs.AsValueEnumerable().SequenceEqual(new[] { "Switch log: Interaction: Lock | KORD-14" }), "Native switch dispatch logs only the authored owned diagnostic");
            Check(InteractionPolicyBindings.InteractionLog(controlSwitch, result) == null, "Unowned switch diagnostic is unaffected");
            token.ThrowIfCancellationRequested();
        }
        catch (Exception error) { throw new InvalidDataException("Interaction probe stopped after " + (checks.Count == 0 ? "setup" : checks[checks.Count - 1]), error); }
        finally
        {
            if (scene.IsValid() && scene.isLoaded) { var unload = SceneManager.UnloadSceneAsync(scene); await UniTask.WaitUntil(() => unload.isDone); }
            if (bundle) bundle!.Unload(true);
            await UniTask.Yield(); Application.logMessageReceived -= Error; Plugin.Log.LogEvent -= Log;
        }
        Check(SceneManager.sceneCount == before, "Scene count restored after unload");
        Check(errors.Count == 0, "No Unity errors during native interaction and cleanup" + (errors.Count == 0 ? "" : ": " + string.Join("\n", errors)));
        return new { passed = true, checks, errors, logs, bundle = input.Sha256,
            scope = "Serialized policies and native Awake, keycard outer unlock completion/cancellation, and switch dispatch. Nested BetterAudio playback and full-map interactions require raid testing." };
    }
}
