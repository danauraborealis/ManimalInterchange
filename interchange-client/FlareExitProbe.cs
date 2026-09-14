using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Comfort.Common;
using Cysharp.Threading.Tasks;
using EFT;
using EFT.Communications;
using EFT.GlobalEvents;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.PrefabSettings;
using EFT.UI;
using HarmonyLib;
using Manimal.Interchange.Components;
using Manimal.Interchange.Shared;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZLinq;

namespace Manimal.Interchange.Client;

internal static class FlareExitProbe
{
    private sealed class Input { public string Bundle = ""; public string Sha256 = ""; public string Scene = ""; }
    internal static async UniTask<object> Run(CancellationToken token)
    {
        var input = JsonConvert.DeserializeObject<Input>(File.ReadAllText(Path.Combine(Plugin.Root, "flare-validation.json")))!;
        var path = ManifestRules.Resolve(Plugin.Root, input.Bundle);
        if (ManifestRules.Hash(path) != input.Sha256) throw new InvalidDataException("Flare fixture changed");
        var beforeScenes = SceneManager.sceneCount;
        var beforeBindings = FlareExitBindings.Count;
        var beforePending = SignalFlareBindings.Pending;
        var beforeVisual = UnityEngine.Object.FindObjectsOfType<VisualSignalFlare>().Length;
        var events = GlobalEventsController.Instance;
        var subscribers = (Dictionary<Type, List<Action<BaseEvent>>>)AccessTools.Field(typeof(GlobalEventsController), "_eventsSubscribes").GetValue(events);
        int Subscribers() => subscribers.TryGetValue(typeof(FlareShootZoneEvent), out var list) ? list.Count : 0;
        var beforeSubscribers = Subscribers();
        var checks = new List<string>(); var errors = new List<string>();
        var notifications = new List<FlareExitBindings.ExitNotification>();
        void Check(bool value, string label) { if (!value) throw new InvalidDataException(label); checks.Add(label); }
        void Error(string message, string stack, LogType kind)
        { if (kind is LogType.Error or LogType.Exception or LogType.Assert) errors.Add(message); }
        AssetBundle? bundle = null; Scene scene = default; IDisposable? scope = null;
        Application.logMessageReceived += Error;
        try
        {
            var request = AssetBundle.LoadFromFileAsync(path); await UniTask.WaitUntil(() => request.isDone);
            bundle = request.assetBundle; Check(bundle, "Actual fixture bundle loads");
            var load = SceneManager.LoadSceneAsync(input.Scene, LoadSceneMode.Additive); await UniTask.WaitUntil(() => load.isDone);
            scene = SceneManager.GetSceneByPath(input.Scene);
            var root = scene.GetRootGameObjects().AsValueEnumerable().Single();
            Check(!root.activeSelf, "Fixture remains inactive before context registration");
            var policy = root.GetComponentInChildren<FlareExitPolicy>(true);
            var owned = (PlayersWithImmuneToSniperFireCollector)policy.Collector;
            var control = root.GetComponentsInChildren<PlayersWithImmuneToSniperFireCollector>(true).AsValueEnumerable().Single(c => c != owned);
            var launcher = policy.Launcher;
            Check(launcher.Direction == Vector3.up && launcher.ShotDelay == 3, "Authored launch direction and delay survive serialization");
            var original = launcher.FlarePrefab.GetComponent<FlareCartridgeSettings>();
            Check(original.InitialSpeed == 55 && original.FlareTimeAfterStart == 1.6f && original.FlareLifetime == 20 && original.Weight == .6f && original.RigidbodyDrag == 1.3f,
                "All recovered projectile motion and lifetime values survive serialization");
            scope = FlareExitBindings.ClaimProbe(scene.handle, new FlareExitBindings.Context
            {
                Side = id => id == "unknown" ? null : id == "scav" ? EPlayerSide.Savage : EPlayerSide.Usec,
                LocalProfile = () => "local",
                Notify = n => { notifications.Add(n); NotificationManager.DisplayNotification(n); }
            });
            root.SetActive(true); await UniTask.Yield();
            Check(Subscribers() == beforeSubscribers + 2, "Both native collectors register their event subscriptions");
            var receive = AccessTools.Method(typeof(PlayersWithImmuneToSniperFireCollector), "CG_Awake");
            void Send(PlayersWithImmuneToSniperFireCollector collector, int kind, string id, int flareType = 2)
            {
                token.ThrowIfCancellationRequested();
                var value = new FlareShootZoneEvent();
                AccessTools.Field(typeof(FlareShootZoneEvent), "_flareEventType").SetValue(value, (FlareEventType)flareType);
                AccessTools.Field(typeof(FlareShootZoneEvent), "_zoneEventType").SetValue(value, (FlareShootZoneEvent.EZoneEventType)kind);
                AccessTools.Field(typeof(FlareShootZoneEvent), "_playerProfileID").SetValue(value, id);
                // Dispatch through the exact native subscriber forwarder. These
                // synthetic player IDs are never published to other raid systems.
                receive.Invoke(collector, new object[] { value });
            }
            Send(control, 3, "control"); Check(control.IsPlayerImmuneForFire("control"), "Unowned collector retains native immunity behavior");
            Send(owned, 3, "wrong", 1); Send(owned, 3, "scav"); Send(owned, 3, "unknown");
            Check(!owned.IsPlayerImmuneForFire("wrong") && !owned.IsPlayerImmuneForFire("scav") && !owned.IsPlayerImmuneForFire("unknown"), "Wrong flare type, scav and missing player rejected");
            Send(owned, 1, "remote"); Check(notifications.Count == 0, "Remote entry has no local notification");
            Send(owned, 1, "local"); Send(owned, 1, "local");
            Check(notifications.Count == 1 && !notifications[0].Success && notifications[0].Duration == ENotificationDurationType.Infinite, "Local entry creates one persistent warning");
            await UniTask.WaitUntil(() => notifications[0].View, cancellationToken: token).Timeout(TimeSpan.FromSeconds(8));
            Check(notifications[0].View!.HasSameNotification(notifications[0]), "Native notification factory displays the owned warning");
            Send(owned, 2, "local"); Check(notifications[0].Dismissed, "Local exit dismisses its own warning");
            Send(owned, 1, "local"); var warning = notifications[1];
            var started = Time.time;
            Send(owned, 3, "local");
            Check(owned.IsPlayerImmuneForFire("local"), "Native sniper immunity query observes successful shot");
            Check(warning.Dismissed && notifications.Count == 3 && notifications[2].Success, "Successful shot replaces the warning with success");
            Check(SignalFlareBindings.Pending == beforePending + 1, "First valid player schedules one response");
            Send(owned, 3, "local"); Send(owned, 4, "party");
            Check(owned.IsPlayerImmuneForFire("party") && SignalFlareBindings.Pending == beforePending + 1, "Party immunity is retained while duplicate shots and response cooldown are respected");
            await UniTask.Delay(1000, cancellationToken: token);
            Check(UnityEngine.Object.FindObjectsOfType<VisualSignalFlare>().Length == beforeVisual, "Response does not appear before the authored delay");
            await UniTask.WaitUntil(() => SignalFlareBindings.Pending == beforePending, cancellationToken: token).Timeout(TimeSpan.FromSeconds(6));
            var visual = UnityEngine.Object.FindObjectsOfType<VisualSignalFlare>().AsValueEnumerable().Single(v => v.gameObject.scene == scene);
            var body = visual.GetComponent<Rigidbody>();
            Check(Time.time - started >= 2.9f && visual.gameObject.scene == scene, "Response appears after delay in the owning scene");
            Check(!body.isKinematic && body.mass == .6f && body.collisionDetectionMode == CollisionDetectionMode.ContinuousDynamic, "Native rigidbody receives the authored physics settings");
            Check(body.velocity.y > 40 && Math.Abs(body.velocity.x) < .01f && Math.Abs(body.velocity.z) < .01f, "Projectile launches upward with native velocity");
            Check(!visual.GetComponent<FlareCartridge>() && visual.gameObject.layer == LayerMask.NameToLayer("Triggers"), "Response uses a visual projectile on the source trigger layer");
            Check(original.GetComponent<MeshRenderer>().enabled && !visual.GetComponent<MeshRenderer>().enabled, "Clone case mesh hides without modifying the original prefab");
            var projectile = visual.transform.Find(original.ProjectileAmmo.name + "(Clone)");
            var effect = visual.transform.Find(original.FlareEffectPrefab.name + "(Clone)");
            Check(projectile && effect, "Both exact prefab children instantiate under the response");
            Check(projectile.gameObject.activeSelf && !effect.gameObject.activeSelf, "Projectile remains visible before ignition");
            await UniTask.WaitUntil(() => effect.gameObject.activeSelf, cancellationToken: token).Timeout(TimeSpan.FromSeconds(4));
            Check(!projectile.gameObject.activeSelf && body.drag == 1.3f, "Ignition swaps projectile for effect and applies drag");
            await UniTask.WaitUntil(() => Time.time >= started + 5.1f, cancellationToken: token);
            Send(owned, 4, "late-party"); Check(SignalFlareBindings.Pending == beforePending + 1, "A new immune player can launch after the five-second cooldown");
            await UniTask.WaitUntil(() => !visual, cancellationToken: token).Timeout(TimeSpan.FromSeconds(23));
            Check(!visual, "Native-clock lifetime destroys the response object");
            Send(owned, 2, "local"); Send(owned, 1, "local");
            Check(notifications[notifications.Count - 1].Success, "Re-entry retains the successful immunity message");
            var cancelled = new FlareExitBindings.ExitNotification(false); cancelled.Dismiss();
            var notifier = UnityEngine.Object.FindObjectOfType<NotifierView>();
            Check(notifier && !(bool)AccessTools.Method(typeof(NotifierView), "ShowNotification").Invoke(notifier, new object[] { cancelled }), "Cancelled queued notification is skipped without blocking the native queue");
            launcher.Launch(); Check(SignalFlareBindings.Pending == beforePending + 1, "Unload cancellation has an outstanding delayed launch");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new InvalidDataException("Flare probe failed after " + checks.Count + " checks; last: " + (checks.Count == 0 ? "none" : checks[checks.Count - 1]) +
                "; Unity errors: " + string.Join(" | ", errors), error);
        }
        finally
        {
            foreach (var notification in notifications) notification.Dismiss();
            if (scene.IsValid() && scene.isLoaded) { var unload = SceneManager.UnloadSceneAsync(scene); if (unload != null) await UniTask.WaitUntil(() => unload.isDone); }
            scope?.Dispose();
            if (bundle) bundle!.Unload(true);
            await UniTask.Yield(); await UniTask.Yield();
            if (Singleton<NotificationManager>.Instantiated)
                foreach (var notification in notifications) Singleton<NotificationManager>.Instance.Notifications.Remove(notification);
            Application.logMessageReceived -= Error;
        }
        Check(SceneManager.sceneCount == beforeScenes, "Scene inventory restored");
        Check(FlareExitBindings.Count == beforeBindings && SignalFlareBindings.Pending == beforePending, "Owned binding and delayed launch cleaned on unload");
        Check(Subscribers() == beforeSubscribers, "Native collector subscriptions removed on unload");
        Check(UnityEngine.Object.FindObjectsOfType<VisualSignalFlare>().Length == beforeVisual, "All response objects belong to the unloaded scene");
        Check(notifications.AsValueEnumerable().All(n => n.Dismissed), "All owned notifications dismissed on cleanup");
        Check(errors.Count == 0, "No Unity errors during the complete sequence");
        return new { Passed = true, BundleSha256 = input.Sha256, CheckCount = checks.Count, Checks = checks, UnityErrors = errors,
            Scope = "Native collector callback, immunity query, real notification UI, delayed visual projectile physics/ignition/lifetime and unload cancellation. Full-map shot detection, source particle prefab, firework audio and extraction remain raid tests." };
    }
}
