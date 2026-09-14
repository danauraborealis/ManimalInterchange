using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.GlobalEvents;
using EFT.Interactive;
using EFT.UI;
using HarmonyLib;
using Manimal.Interchange.Components;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.Interchange.Client;

internal static class FlareExitBindings
{
    internal sealed class Context
    {
        internal Func<string, EPlayerSide?> Side = null!;
        internal Func<string?> LocalProfile = null!;
        internal Action<ExitNotification> Notify = NotificationManager.DisplayNotification;
    }
    private static readonly Dictionary<int, Context> ProbeContexts = new();
    private static readonly Dictionary<FlareExitPolicy, Binding> Bindings = new();
    private static readonly FieldInfo ImmunePlayers = AccessTools.Field(typeof(PlayersWithImmuneToSniperFireCollector), "hashSet_0");
    internal static int Count => Bindings.Count;
    internal static void Enable() => FlareExitPolicy.Destroyed += Remove;
    internal static void Disable()
    {
        FlareExitPolicy.Destroyed -= Remove;
        foreach (var binding in Bindings.Values) binding.Dispose();
        Bindings.Clear();
    }
    private sealed class Scope : IDisposable
    {
        private readonly int _handle;
        internal Scope(int handle) => _handle = handle;
        public void Dispose() => ProbeContexts.Remove(_handle);
    }
    internal static IDisposable ClaimProbe(int scene, Context context)
    { ProbeContexts.Add(scene, context); return new Scope(scene); }
    private static void Remove(FlareExitPolicy policy)
    {
        if (!Bindings.TryGetValue(policy, out var binding)) return;
        Bindings.Remove(policy); binding.Dispose();
    }
    internal static bool Handle(PlayersWithImmuneToSniperFireCollector collector, FlareShootZoneEvent value)
    {
        if (!collector.TryGetComponent<FlareExitPolicy>(out var policy) || policy.Collector != collector) return false;
        if (!Bindings.TryGetValue(policy, out var binding))
        {
            var context = ProbeContexts.TryGetValue(policy.gameObject.scene.handle, out var probe) ? probe : new Context
            {
                Side = id =>
                {
                    if (!Singleton<GameWorld>.Instantiated) return null;
                    var player = Singleton<GameWorld>.Instance.GetAlivePlayerByProfileID(id);
                    return player ? player.Side : null;
                },
                LocalProfile = () => GamePlayerOwner.MyPlayer ? GamePlayerOwner.MyPlayer.ProfileId : null
            };
            binding = new Binding(policy, (HashSet<string>)ImmunePlayers.GetValue(collector), context);
            Bindings.Add(policy, binding);
        }
        if (value.FlareEventType == collector.flareTypeForImmune) binding.Handle(value);
        return true;
    }
    private sealed class Binding : IDisposable
    {
        private readonly FlareExitPolicy _policy;
        private readonly HashSet<string> _immune;
        private readonly Context _context;
        private float _nextResponse = float.NegativeInfinity;
        private bool _inside, _success;
        private ExitNotification? _notification;
        internal Binding(FlareExitPolicy policy, HashSet<string> immune, Context context)
        { _policy = policy; _immune = immune; _context = context; }
        internal void Handle(FlareShootZoneEvent value)
        {
            var id = value.PlayerProfileID;
            if (string.IsNullOrEmpty(id)) return;
            var side = _context.Side(id);
            // Retail IPlayer slot 2 is Side; its explicit excluded value 4 is Savage.
            if (!side.HasValue || side.Value == EPlayerSide.Savage) return;
            var local = id == _context.LocalProfile();
            switch ((int)value.ZoneEventType)
            {
                case 1:
                    if (local && !_inside) { _inside = true; Show(); }
                    break;
                case 2:
                    if (local) { _inside = false; Hide(); }
                    break;
                case 3:
                case 4:
                    if (!_immune.Add(id)) return;
                    if (local) { _success = true; Hide(); if (_inside) Show(); }
                    if (_policy.Launcher && Time.time >= _nextResponse)
                    {
                        _nextResponse = Time.time + 5f;
                        _policy.Launcher.Launch();
                    }
                    break;
            }
        }
        private void Show()
        {
            // Each display owns a distinct identity. A cancelled queued message
            // cannot become visible again when the player re-enters the zone.
            _notification = new ExitNotification(_success);
            _context.Notify(_notification);
        }
        private void Hide() { _notification?.Dismiss(); _notification = null; }
        public void Dispose() => Hide();
    }

    internal sealed class ExitNotification : FireFlareForExitNotification
    {
        private BaseNotificationView? _view;
        internal bool Success { get; }
        internal bool Dismissed { get; private set; }
        internal BaseNotificationView? View => _view;
        internal ExitNotification(bool success) { Success = success; Duration = ENotificationDurationType.Infinite; }
        public override bool ShowNotification => !Dismissed;
        public override string Description
        {
            get
            {
                if (!Success) return base.Description;
                const string key = "Notification/SniperFlareZoneDisabled";
                var localized = key.Localized();
                // Exact upstream English text until the coordinated server data
                // supplies the locale entry for the selected language.
                return localized == key ? "Flare signal received. You can extract safely!" : localized;
            }
        }
        public override ENotificationIconType Icon => Success ? (ENotificationIconType)0 : base.Icon;
        public override BaseNotificationView CreateView(INotificationViewFactory factory)
        { _view = base.CreateView(factory); return _view; }
        internal void Dismiss()
        {
            Dismissed = true;
            // A pooled view may have been reused after another UI transition.
            if (_view && _view!.HasSameNotification(this) && !_view.IsHiding) _view.HideNotification(false);
            _view = null;
        }
    }
}

internal sealed class FlareExitEventPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(PlayersWithImmuneToSniperFireCollector), "method_0");
    [PatchPrefix]
    private static bool Prefix(PlayersWithImmuneToSniperFireCollector __instance, FlareShootZoneEvent __0)
        => !FlareExitBindings.Handle(__instance, __0);
}

internal sealed class FlareExitQueuedNotificationPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(NotifierView), "ShowNotification");
    [PatchPrefix]
    private static bool Prefix(Notification __0, ref bool __result)
    {
        if (__0 is not FlareExitBindings.ExitNotification { Dismissed: true }) return true;
        // False clears native _isProcessingQueuedNotification, allowing the next
        // queued notification to proceed without allocating a cancelled view.
        __result = false; return false;
    }
}
