using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using EFT.GameTriggers;
using EFT.InventoryLogic;
using HarmonyLib;
using Manimal.Interchange.Components;
using Manimal.Interchange.Shared;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZLinq;

namespace Manimal.Interchange.Client;

internal static class TriggerRelayProbe
{
    private sealed class Input { public string Bundle = ""; public string Sha256 = ""; public string Scene = ""; }
    internal static async UniTask<object> Run(CancellationToken token)
    {
        var input = JsonConvert.DeserializeObject<Input>(File.ReadAllText(Path.Combine(Plugin.Root, "trigger-validation.json")))
            ?? throw new InvalidDataException("Trigger fixture manifest missing");
        var path = ManifestRules.Resolve(Plugin.Root, input.Bundle);
        if (ManifestRules.Hash(path) != input.Sha256) throw new InvalidDataException("Trigger fixture changed");
        var checks = new List<string>();
        var errors = new List<string>();
        var outputs = new List<(string Trigger, string? Origin)>();
        var notifications = new List<string>();
        void Check(bool pass, string name) { if (!pass) throw new InvalidDataException(name); checks.Add(name); }
        void Error(string message, string stack, LogType kind)
        { if (kind is LogType.Error or LogType.Exception or LogType.Assert) errors.Add(message); }
        var uiType = typeof(TriggersEmitter).Assembly.GetType("EFT.UI.ItemUiContext", true);
        var inventoryField = AccessTools.Field(uiType, "_inventoryController");
        var inventory = Resources.FindObjectsOfTypeAll(uiType).AsValueEnumerable()
            .Select(ui => inventoryField.GetValue(ui) as InventoryController).FirstOrDefault(c => c != null);
        Check(inventory != null, "Existing menu inventory controller is available for read-only possession checks");
        var realInventory = inventory!.Inventory;
        string[] Snapshot() => realInventory.GetPlayerItems((EPlayerItems)63).AsValueEnumerable()
            .Select(i => i.Id.ToString() + ":" + i.StackObjectsCount).ToArray();
        var beforeInventory = Snapshot();
        var firstItem = realInventory.GetPlayerItems((EPlayerItems)63).AsValueEnumerable().First();
        var presentTemplate = firstItem.TemplateId.ToString();
        const string absentTemplate = "000000000000000000000000";
        Check(!realInventory.GetAllItemByTemplate(absentTemplate).AsValueEnumerable().Any(), "Missing-item fixture template is absent from the native inventory");
        var present = new TriggerRelayBindings.ItemRequirement(presentTemplate);
        var absent = new TriggerRelayBindings.ItemRequirement(absentTemplate);
        Check(present.Check(inventory, null!) && present.TryRedeem(inventory, null!), "Real native inventory possession passes check and redeem");
        Check(!absent.Check(inventory, null!) && !absent.TryRedeem(inventory, null!), "Real native inventory absence fails check and redeem");
        Check(Snapshot().AsValueEnumerable().SequenceEqual(beforeInventory), "Possession checks do not consume or change inventory items");
        var beforeScenes = SceneManager.sceneCount;
        var beforeBindings = TriggerRelayBindings.Count;
        var emitterType = typeof(TriggersEmitter).Assembly.GetType("EFT.GameTriggers.LocalTriggersEmitter", true);
        var emitter = (TriggersEmitter)Activator.CreateInstance(emitterType);
        var actions = (Dictionary<int, List<Action<TriggerEvent>>>)AccessTools.Field(typeof(TriggersEmitter), "_actionsMap").GetValue(emitter);
        int CallbackCount() => actions.Values.AsValueEnumerable().Sum(list => list.Count);
        var moduleAvailable = true;
        var localProfile = "MI_probe_local";
        var context = new TriggerRelayBindings.Context
        {
            Emitter = emitter,
            HasModule = () => moduleAvailable,
            LocalProfileId = () => localProfile,
            Redeem = (requirement, origin) => origin == localProfile && requirement.TryRedeem(inventory, null!),
            Notify = message => notifications.Add(message)
        };
        AssetBundle? bundle = null;
        Scene scene = default;
        IDisposable? scope = null;
        var observerCallbacks = 0;
        Application.logMessageReceived += Error;
        try
        {
            token.ThrowIfCancellationRequested();
            var request = AssetBundle.LoadFromFileAsync(path);
            await UniTask.WaitUntil(() => request.isDone);
            bundle = request.assetBundle;
            if (!bundle) throw new InvalidDataException("Trigger bundle failed to load");
            Check(bundle.GetAllScenePaths().AsValueEnumerable().SequenceEqual(new[] { input.Scene }), "Exact trigger fixture scene");
            var load = SceneManager.LoadSceneAsync(input.Scene, LoadSceneMode.Additive);
            await UniTask.WaitUntil(() => load.isDone);
            scene = SceneManager.GetSceneByPath(input.Scene);
            var roots = scene.GetRootGameObjects();
            Check(roots.Length == 1 && !roots[0].activeSelf, "Fixture remains inactive until configured");
            var relays = roots[0].GetComponentsInChildren<TriggerRelay>(true).AsValueEnumerable().OrderBy(r => r.SourceKey).ToArray();
            Check(relays.Length == 7 && relays[0].Chance == .05f, "Seven exact runtime relay components and source chance deserialized");
            Check(relays[3].RequiredItemTemplates[0] == "6877c84d020406d3ea060551", "Serialized source note requirement identity preserved");
            relays[3].RequiredItemTemplates = new[] { presentTemplate };
            relays[4].RequiredItemTemplates = new[] { absentTemplate };
            foreach (var relay in relays)
            {
                var success = relay.Success;
                var failure = relay.Failure;
                emitter.Subscribe(success, e => outputs.Add((success, e.OriginProfileId)));
                emitter.Subscribe(failure, e => outputs.Add((failure, e.OriginProfileId)));
            }
            var unrelatedCalls = 0;
            emitter.Subscribe(relays[0].Input, () => unrelatedCalls++);
            observerCallbacks = CallbackCount();
            scope = TriggerRelayBindings.ClaimProbe(scene.handle, context);
            roots[0].SetActive(true);
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
            Check(TriggerRelayBindings.Count == beforeBindings + 7 && CallbackCount() == observerCallbacks + 7, "Natural Start registers exactly one native emitter handler per relay");
            void Emit(int index, string? origin, string? expected, string name)
            {
                token.ThrowIfCancellationRequested();
                outputs.Clear();
                emitter.Emit(relays[index].Input, origin!);
                Check(expected == null ? outputs.Count == 0 : outputs.Count == 1 && outputs[0].Trigger == expected && outputs[0].Origin == origin, name);
            }
            Check(relays[0].SelectRandomOutcome(.05f) == relays[0].Success, "Inclusive source chance boundary succeeds");
            Check(relays[0].SelectRandomOutcome(.050001f) == relays[0].Failure, "Above-chance boundary fails");
            Check(relays[0].SelectRandomOutcome(0) == relays[0].Success && relays[0].SelectRandomOutcome(1) == relays[0].Failure, "Chance range endpoints preserved");
            Emit(2, localProfile, relays[2].Success, "Native emitter routes guaranteed success with original profile");
            var nativeRoll = context.Roll;
            context.Roll = () => .5f;
            Emit(1, localProfile, relays[1].Failure, "Native random range routes zero-chance failure");
            context.Roll = nativeRoll;
            moduleAvailable = false;
            Emit(2, localProfile, null, "No random output without native trigger module");
            Emit(3, localProfile, null, "No requirement output without native trigger module");
            moduleAvailable = true;
            Emit(3, localProfile, relays[3].Success, "Native inventory possession emits success");
            Emit(4, localProfile, relays[4].Failure, "Native inventory absence emits failure");
            Emit(3, "MI_probe_other", relays[3].Failure, "Unresolved origin cannot redeem another profile's item");
            Emit(3, null, null, "Requirement ignores missing player origin");
            emitter.SetTriggerState(relays[3].Success, false);
            Emit(3, localProfile, null, "Native emitter respects disabled output trigger");
            emitter.SetTriggerState(relays[3].Success, true);
            Emit(3, localProfile, relays[3].Success, "Native emitter resumes enabled output trigger");
            emitter.Emit(relays[5].Input, "MI_probe_other");
            Check(notifications.Count == 0, "Local notification ignores another player");
            emitter.Emit(relays[5].Input, localProfile);
            Check(notifications.Count == 1 && notifications[0] == "Requires ID Card", "Local origin receives exact source notification text");
            emitter.Emit(relays[6].Input, "MI_probe_other");
            Check(notifications.Count == 2, "All-player notification preserves broadcast policy");
            UnityEngine.Object.Destroy(relays[0].gameObject);
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
            Check(TriggerRelayBindings.Count == beforeBindings + 6 && CallbackCount() == observerCallbacks + 6, "Individual destruction removes only its relay subscription");
            outputs.Clear();
            emitter.Emit("MI_trigger_input_0", localProfile);
            Check(outputs.Count == 0 && unrelatedCalls == 1, "Unrelated subscriber survives relay destruction");
        }
        finally
        {
            if (scene.IsValid() && scene.isLoaded)
            {
                var unload = SceneManager.UnloadSceneAsync(scene);
                if (unload != null) await UniTask.WaitUntil(() => unload.isDone);
            }
            scope?.Dispose();
            if (bundle != null) bundle.Unload(true);
            emitter.Dispose();
            Application.logMessageReceived -= Error;
        }
        Check(TriggerRelayBindings.Count == beforeBindings, "All relay registrations removed after scene unload");
        Check(SceneManager.sceneCount == beforeScenes, "Original menu scene count restored");
        Check(Snapshot().AsValueEnumerable().SequenceEqual(beforeInventory), "All runtime tests preserve real inventory item identities and quantities");
        Check(errors.Count == 0, "No Unity errors: " + string.Join("; ", errors));
        return new { Passed = true, BundleSha256 = input.Sha256, CheckCount = checks.Count, Checks = checks, UnityErrors = errors,
            Scope = "Actual native local emitter dispatch, real inventory possession without consumption, origin/output policies, source chance boundary, and natural subscription cleanup. Notification text/filter verified through a captured sink; actual raid UI and module lookup remain full-map tests." };
    }
}
