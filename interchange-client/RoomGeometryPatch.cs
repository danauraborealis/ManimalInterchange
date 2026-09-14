using System;
using System.Reflection;
using Audio.SpatialSystem;
using HarmonyLib;
using Manimal.Interchange.Components;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.Interchange.Client;

internal sealed class RoomGeometryPatch : ModulePatch
{
    internal static MethodInfo QueryMethod => AccessTools.Method(
        typeof(SpatialAudioRoom).Assembly.GetType("Audio.SpatialSystem.AudioRoomStorage", true),
        "CheckPositionInsideRoom", new[] { typeof(ISpatialAudioRoom), typeof(Vector3) });

    protected override MethodBase GetTargetMethod() => QueryMethod;

    [PatchPrefix]
    private static bool Prefix(ISpatialAudioRoom __0, Vector3 __1, ref bool __result)
    {
        if (__0 is not SpatialAudioRoom room || !room.TryGetComponent<RoomGeometry>(out var geometry) || !geometry.enabled) return true;
        if (geometry.SourceRoomId != room.ID)
            throw new InvalidOperationException("Interchange room geometry identity differs: " + geometry.SourceKey);
        __result = geometry.ContainsPoint(__1);
        return false;
    }
}
