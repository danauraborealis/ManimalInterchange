using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.GameTriggers;
using EFT.InventoryLogic;
using HarmonyLib;
using Manimal.Interchange.Components;
using ZLinq;

namespace Manimal.Interchange.Client;

internal static class TriggerRelayBindings
{
    internal sealed class Context
    {
        internal TriggersEmitter Emitter = null!;
        internal Func<ITriggerRequirement, string, bool> Redeem = null!;
        internal Func<bool> HasModule = null!;
        internal Func<string?> LocalProfileId = null!;
        internal Action<string> Notify = null!;
        internal Func<float> Roll = () => MyExtensions.Random(0, 1);
    }

    private static readonly Dictionary<TriggerRelay, Binding> Bindings = new();
    private static readonly Dictionary<int, Context> ProbeContexts = new();
    private static readonly FieldInfo Actions = AccessTools.Field(typeof(TriggersEmitter), "_actionsMap");
    internal static int Count => Bindings.Count;
    private sealed class Scope : IDisposable
    {
        private readonly int _handle;
        internal Scope(int handle) => _handle = handle;
        public void Dispose() => ProbeContexts.Remove(_handle);
    }
    internal static IDisposable ClaimProbe(int sceneHandle, Context context)
    {
        ProbeContexts.Add(sceneHandle, context);
        return new Scope(sceneHandle);
    }
    internal static void Enable() { TriggerRelay.Started += Register; TriggerRelay.Destroyed += Remove; }
    internal static void Disable()
    {
        TriggerRelay.Started -= Register; TriggerRelay.Destroyed -= Remove;
        foreach (var binding in Bindings.Values) binding.Dispose();
        Bindings.Clear();
    }

    private static Context NativeContext()
    {
        if (!Singleton<GameWorld>.Instantiated) throw new InvalidOperationException("Trigger relay started outside a game world");
        var world = Singleton<GameWorld>.Instance;
        return new Context
        {
            Emitter = world.TriggersEmitter,
            HasModule = () => world && world.TriggersModule,
            Redeem = (requirement, profile) => world.TriggersModule && world.TriggersModule.TryRedeem(requirement, profile),
            LocalProfileId = () => world && world.MainPlayer ? world.MainPlayer.ProfileId : null,
            Notify = text => NotificationManager.DisplayMessageNotification(text.Localized())
        };
    }
    private static void Register(TriggerRelay relay)
    {
        if (Bindings.ContainsKey(relay)) return;
        if (string.IsNullOrEmpty(relay.Input)) throw new InvalidOperationException("Trigger relay input is empty: " + relay.SourceKey);
        var context = ProbeContexts.TryGetValue(relay.gameObject.scene.handle, out var probe) ? probe : NativeContext();
        var binding = new Binding(relay, context);
        Bindings.Add(relay, binding);
        try { binding.Subscribe(); }
        catch { Bindings.Remove(relay); binding.Dispose(); throw; }
    }
    private static void Remove(TriggerRelay relay)
    {
        if (!Bindings.TryGetValue(relay, out var binding)) return;
        Bindings.Remove(relay);
        binding.Dispose();
    }

    internal sealed class ItemRequirement : ITriggerRequirement
    {
        internal string TemplateId { get; }
        internal ItemRequirement(string templateId) => TemplateId = templateId;
        public void Start() { }
        public void Dispose() { }
        public bool Check(InventoryController inventoryController, SkillManager skillManager)
            => inventoryController.Inventory.GetAllItemByTemplate(TemplateId).AsValueEnumerable().Any();
        // Retail item requirements check possession and do not consume the item.
        public bool TryRedeem(InventoryController inventoryController, SkillManager skillManager) => Check(inventoryController, skillManager);
    }

    private sealed class Binding : IDisposable
    {
        private readonly TriggerRelay _relay;
        private readonly Context _context;
        private readonly Action<TriggerEvent> _handler;
        private readonly ItemRequirement[] _requirements;
        private List<Action<TriggerEvent>>? _callbacks;
        internal Binding(TriggerRelay relay, Context context)
        {
            _relay = relay; _context = context; _handler = Handle;
            _requirements = relay.RequiredItemTemplates.AsValueEnumerable().Select(t => new ItemRequirement(t)).ToArray();
        }
        internal void Subscribe()
        {
            _context.Emitter.Subscribe(_relay.Input, _handler);
            var map = (Dictionary<int, List<Action<TriggerEvent>>>)Actions.GetValue(_context.Emitter);
            // Use the list created by the native subscription API so teardown
            // removes only our exact delegate; unrelated handlers remain owned.
            foreach (var list in map.Values)
                if (list.Contains(_handler)) { _callbacks = list; return; }
            throw new InvalidOperationException("Native trigger subscription was not retained");
        }
        private void Handle(TriggerEvent trigger)
        {
            var origin = trigger.OriginProfileId;
            switch (_relay.Kind)
            {
                case TriggerRelayKind.RandomChance:
                    if (!_context.HasModule()) return;
                    _context.Emitter.Emit(_relay.SelectRandomOutcome(_context.Roll()), origin);
                    break;
                case TriggerRelayKind.ItemRequirement:
                    if (origin == null || !_context.HasModule()) return;
                    foreach (var requirement in _requirements)
                        if (!_context.Redeem(requirement, origin)) { _context.Emitter.Emit(_relay.Failure, origin); return; }
                    _context.Emitter.Emit(_relay.Success, origin);
                    break;
                case TriggerRelayKind.PlayerNotification:
                    var local = _context.LocalProfileId();
                    if (_relay.NotificationForAll || local != null && origin == local) _context.Notify(_relay.Message);
                    break;
                default: throw new InvalidOperationException("Unknown Interchange trigger relay");
            }
        }
        public void Dispose() { _callbacks?.Remove(_handler); _callbacks = null; }
    }
}
