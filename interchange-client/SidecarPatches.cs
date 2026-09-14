using System.Reflection;
using Audio.SpatialSystem;
using HarmonyLib;
using Koenigz.PerfectCulling.EFT;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.Interchange.Client;

internal sealed class CullingSidecarPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(PackedCullingGridData), nameof(PackedCullingGridData.GetPackedFilePathForGrid));
    [PatchPostfix]
    private static void Postfix(PerfectCullingAdaptiveGrid grid, ref string __result)
        => InterchangeSidecars.PackedPath(grid, ref __result);
}

internal sealed class AudioBakeSidecarPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Constructor(typeof(SpatialAudioDataLoader), new[] { typeof(string), typeof(MonoBehaviour) });
    [PatchPrefix]
    private static void Prefix(ref string dataPath, MonoBehaviour runner) => InterchangeSidecars.AudioPath(ref dataPath, runner);
}

internal sealed class AcousticMapPathPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.PropertyGetter(typeof(MetaXRAcousticMap), "AbsoluteFilePath");
    [PatchPostfix]
    private static void Postfix(Component __instance, ref string __result) => InterchangeSidecars.XrAbsolutePath(__instance, ref __result);
}

internal sealed class AcousticMapLoadPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(MetaXRAcousticMap), "LoadMapAsync", new[] { typeof(string) });
    [PatchPrefix]
    private static void Prefix(Component __instance, ref string __0) => InterchangeSidecars.XrAsyncPath(__instance, ref __0);
}

internal sealed class AcousticGeometryPathPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.PropertyGetter(typeof(MetaXRAcousticGeometry), "AbsoluteFilePath");
    [PatchPostfix]
    private static void Postfix(Component __instance, ref string __result) => InterchangeSidecars.XrAbsolutePath(__instance, ref __result);
}

internal sealed class AcousticGeometryLoadPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(MetaXRAcousticGeometry), "LoadGeometryAsync", new[] { typeof(string) });
    [PatchPrefix]
    private static void Prefix(Component __instance, ref string __0) => InterchangeSidecars.XrAsyncPath(__instance, ref __0);
}
