using System;
using System.IO;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using Cysharp.Threading.Tasks;
using EFT;
using Manimal.Interchange.Shared;
using Newtonsoft.Json;
using UnityEngine.SceneManagement;

namespace Manimal.Interchange.Client;

[BepInPlugin(ModIdentity.Guid, ModIdentity.ClientName, ModIdentity.Version)]
[BepInDependency("com.arys.unitytoolkit", ModIdentity.ToolkitMinimumVersion)]
[BepInDependency("xyz.drakia.waypoints", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("com.acidphantasm.botplacementsystem", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("com.Amanda.Graphics", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    internal static string Root = "";
    internal static ManualLogSource Log = null!;
    internal static ConfigEntry<bool> AllowProbe = null!;
    internal static ConfigEntry<bool> AllowFullMapTest = null!;
    internal static ConfigEntry<bool> RebindNativeShaders = null!;
    internal static ConfigEntry<bool> RebindMaterialShaders = null!;
    private CancellationTokenSource? _probeCancellation;
    public string ProbeStatus { get; private set; } = "idle";
    public string ProbeReport { get; private set; } = "";
    public int ProbeReportLength => ProbeReport.Length;

    public string ReadProbeReportChunk(int offset, int count)
    {
        if (offset < 0 || offset > ProbeReport.Length || count < 1 || count > 2048)
            throw new ArgumentOutOfRangeException();
        return ProbeReport.Substring(offset, Math.Min(count, ProbeReport.Length - offset));
    }

    private void Awake()
    {
        Root = Path.GetDirectoryName(Info.Location)!;
        Log = Logger;
        InterchangeAmandsCompatibility.EnableIfAvailable();
        AllowProbe = Config.Bind("Development", "AllowLoaderProbe", false, "Allow explicit menu-only scene loading tests through the AI Bridge.");
        AllowFullMapTest = Config.Bind("Development", "AllowFullMapTest", false, "Allow full replacement raids with a matching server test manifest.");
        RebindNativeShaders = Config.Bind("Rendering", "RebindNativeShaders", true, "Point the map's screen-space helper shaders (ambient, wet, tonemapper) at the game's own copies instead of the bundled retail programs.");
        RebindMaterialShaders = Config.Bind("Rendering", "RebindMaterialShaders", true, "Point map materials at the game's own shader of the same name where one exists.");
        new LoadPresetReversePatch().Enable();
        new LoadPresetPatch().Enable();
        new BundledScenePatch().Enable();
        new CullingSidecarPatch().Enable();
        new AudioBakeSidecarPatch().Enable();
        new AcousticMapPathPatch().Enable();
        new AcousticMapLoadPatch().Enable();
        new AcousticGeometryPathPatch().Enable();
        new AcousticGeometryLoadPatch().Enable();
        new RoomGeometryPatch().Enable();
        new AreaLightInitPatch().Enable();
        new TerrainSeasonPatch().Enable();
        new BallisticTriggerPatch().Enable();
        new CloudLayerPatch().Enable();
        new EffectSoundPolicyPatch().Enable();
        new DistanceAudioFadePatch().Enable();
        new OwnedSoundGagPatch().Enable();
        new AdvancedSoundPolicyPatch().Enable();
        new FlareExitEventPatch().Enable();
        new FlareExitQueuedNotificationPatch().Enable();
        new DoorRelatedTriggerPatch().Enable();
        new KeycardOperationPolicyPatch().Enable();
        new SwitchInteractionLogPatch().Enable();
        new SyncLoopInitPatch().Enable();
        new SyncLoopPlayPatch().Enable();
        new SyncLoopStopPatch().Enable();
        new WaterRendererPatch().Enable();
        new DistantShadowCacheParametersPatch().Enable();
        new DistantShadowOrthoMatrixPatch().Enable();
        new DistantShadowTransformPatch().Enable();
        new NativeCameraPrefabPatch().Enable();
        SyncLoopBindings.Enable();
        EnvironmentAudioBindings.Enable();
        AmbientBlendBindings.Enable();
        LevelLightingBindings.Enable();
        AudioBindings.Enable();
        AdvancedSoundBindings.Enable();
        SignalFlareBindings.Enable();
        FlareExitBindings.Enable();
        SeasonBindings.Enable();
        TriggerRelayBindings.Enable();
        NativeShaderBindings.Enable();
        SceneManager.sceneUnloaded += AreaLightBindings.SceneUnloaded;
        SceneManager.sceneUnloaded += SceneLoader.SceneUnloaded;
        InterchangeWaypointsCompatibility.EnableIfAvailable();
        InterchangeBotPlacementCompatibility.EnableIfAvailable();
        Log.LogInfo(ModIdentity.ClientName + " " + ModIdentity.Version + ": replacement content requires a matching server manifest.");
    }

    public string BeginLoaderProbe()
    {
        if (!AllowProbe.Value) throw new InvalidOperationException("Loader probe is disabled in client configuration");
        if (Singleton<GameWorld>.Instantiated) throw new InvalidOperationException("Run loader probes from the main menu, outside a raid");
        if (_probeCancellation != null) throw new InvalidOperationException("A loader probe is already running");
        _probeCancellation = new CancellationTokenSource();
        ProbeStatus = "running";
        ProbeReport = "";
        RunProbe(_probeCancellation.Token).Forget();
        return ProbeStatus;
    }

    public void CancelLoaderProbe() => _probeCancellation?.Cancel();

    public string BeginFullMapRaidProbe()
    {
        if (!AllowProbe.Value) throw new InvalidOperationException("Development probes are disabled");
        if (_probeCancellation != null) throw new InvalidOperationException("A development probe is already running");
        _probeCancellation = new CancellationTokenSource(); ProbeStatus = "running"; ProbeReport = "";
        RunFullMapRaidProbe(_probeCancellation.Token).Forget(); return ProbeStatus;
    }

    private async UniTaskVoid RunFullMapRaidProbe(CancellationToken token)
    {
        try { ProbeReport = JsonConvert.SerializeObject(await FullMapRaidProbe.Run(token), Formatting.Indented); ProbeStatus = "completed"; }
        catch (OperationCanceledException) { ProbeStatus = "cancelled"; }
        catch (Exception error) { ProbeStatus = "failed"; ProbeReport = error.ToString(); Log.LogError(error); }
        finally { _probeCancellation?.Dispose(); _probeCancellation = null; }
    }

    public string BeginPausedCancellationProbe()
    {
        if (!AllowProbe.Value) throw new InvalidOperationException("Development probes are disabled");
        if (Singleton<GameWorld>.Instantiated) throw new InvalidOperationException("Run cancellation validation from the main menu");
        if (_probeCancellation != null) throw new InvalidOperationException("A development probe is already running");
        _probeCancellation = new CancellationTokenSource(); ProbeStatus = "running"; ProbeReport = "";
        RunPausedCancellationProbe(_probeCancellation.Token).Forget(); return ProbeStatus;
    }

    private async UniTaskVoid RunPausedCancellationProbe(CancellationToken token)
    {
        try { ProbeReport = JsonConvert.SerializeObject(await SceneLoader.RunPausedCancellationProbe(token), Formatting.Indented); ProbeStatus = "passed"; }
        catch (OperationCanceledException) { ProbeStatus = "cancelled"; }
        catch (Exception error) { ProbeStatus = "failed"; ProbeReport = error.ToString(); Log.LogError(error); }
        finally { _probeCancellation?.Dispose(); _probeCancellation = null; }
    }

    public string BeginRenderProbe()
    {
        if (!AllowProbe.Value) throw new InvalidOperationException("Development probes are disabled");
        if (Singleton<GameWorld>.Instantiated) throw new InvalidOperationException("Run render validation from the main menu");
        if (_probeCancellation != null) throw new InvalidOperationException("A development probe is already running");
        _probeCancellation = new CancellationTokenSource(); ProbeStatus = "running"; ProbeReport = "";
        RunRenderProbe(_probeCancellation.Token).Forget(); return ProbeStatus;
    }

    public string BeginFlareProbe()
    {
        if (!AllowProbe.Value) throw new InvalidOperationException("Development probes are disabled");
        if (Singleton<GameWorld>.Instantiated) throw new InvalidOperationException("Run flare validation from the main menu");
        if (_probeCancellation != null) throw new InvalidOperationException("A development probe is already running");
        _probeCancellation = new CancellationTokenSource(); ProbeStatus = "running"; ProbeReport = "";
        RunFlareProbe(_probeCancellation.Token).Forget(); return ProbeStatus;
    }

    public string BeginInteractionProbe()
    {
        if (!AllowProbe.Value) throw new InvalidOperationException("Development probes are disabled");
        if (Singleton<GameWorld>.Instantiated) throw new InvalidOperationException("Run interaction validation from the main menu");
        if (_probeCancellation != null) throw new InvalidOperationException("A development probe is already running");
        _probeCancellation = new CancellationTokenSource(); ProbeStatus = "running"; ProbeReport = "";
        RunInteractionProbe(_probeCancellation.Token).Forget(); return ProbeStatus;
    }

    public string BeginSyncLoopProbe()
    {
        if (!AllowProbe.Value) throw new InvalidOperationException("Development probes are disabled");
        if (Singleton<GameWorld>.Instantiated) throw new InvalidOperationException("Run alarm validation from the main menu");
        if (_probeCancellation != null) throw new InvalidOperationException("A development probe is already running");
        _probeCancellation = new CancellationTokenSource(); ProbeStatus = "running"; ProbeReport = "";
        RunSyncLoopProbe(_probeCancellation.Token).Forget(); return ProbeStatus;
    }

    public string BeginWaterProbe()
    {
        if (!AllowProbe.Value) throw new InvalidOperationException("Development probes are disabled");
        if (Singleton<GameWorld>.Instantiated) throw new InvalidOperationException("Run water validation from the main menu");
        if (_probeCancellation != null) throw new InvalidOperationException("A development probe is already running");
        _probeCancellation = new CancellationTokenSource(); ProbeStatus = "running"; ProbeReport = "";
        RunWaterProbe(_probeCancellation.Token).Forget(); return ProbeStatus;
    }

    public string BeginNativeCameraProbe()
    {
        if (!AllowProbe.Value) throw new InvalidOperationException("Development probes are disabled");
        if (Singleton<GameWorld>.Instantiated) throw new InvalidOperationException("Run camera validation from the main menu");
        if (_probeCancellation != null) throw new InvalidOperationException("A development probe is already running");
        _probeCancellation = new CancellationTokenSource(); ProbeStatus = "running"; ProbeReport = "";
        RunNativeCameraProbe(_probeCancellation.Token).Forget(); return ProbeStatus;
    }

    public string BeginAmbientBlendProbe()
    {
        if (!AllowProbe.Value) throw new InvalidOperationException("Development probes are disabled");
        if (Singleton<GameWorld>.Instantiated) throw new InvalidOperationException("Run ambient validation from the main menu");
        if (_probeCancellation != null) throw new InvalidOperationException("A development probe is already running");
        _probeCancellation = new CancellationTokenSource(); ProbeStatus = "running"; ProbeReport = "";
        RunAmbientBlendProbe(_probeCancellation.Token).Forget(); return ProbeStatus;
    }

    private async UniTaskVoid RunAmbientBlendProbe(CancellationToken token)
    {
        try { ProbeReport = JsonConvert.SerializeObject(await AmbientBlendProbe.Run(token), Formatting.Indented); ProbeStatus = "passed"; }
        catch (OperationCanceledException) { ProbeStatus = "cancelled"; }
        catch (Exception error) { ProbeStatus = "failed"; ProbeReport = error.ToString(); Log.LogError(error); }
        finally { _probeCancellation?.Dispose(); _probeCancellation = null; }
    }

    private async UniTaskVoid RunNativeCameraProbe(CancellationToken token)
    {
        try { ProbeReport = JsonConvert.SerializeObject(await NativeCameraProbe.Run(token), Formatting.Indented); ProbeStatus = "passed"; }
        catch (OperationCanceledException) { ProbeStatus = "cancelled"; }
        catch (Exception error) { ProbeStatus = "failed"; ProbeReport = error.ToString(); Log.LogError(error); }
        finally { _probeCancellation?.Dispose(); _probeCancellation = null; }
    }

    private async UniTaskVoid RunWaterProbe(CancellationToken token)
    {
        try { ProbeReport = JsonConvert.SerializeObject(await WaterRendererProbe.Run(token), Formatting.Indented); ProbeStatus = "passed"; }
        catch (OperationCanceledException) { ProbeStatus = "cancelled"; }
        catch (Exception error) { ProbeStatus = "failed"; ProbeReport = error.ToString(); Log.LogError(error); }
        finally { _probeCancellation?.Dispose(); _probeCancellation = null; }
    }

    private async UniTaskVoid RunSyncLoopProbe(CancellationToken token)
    {
        try { ProbeReport = JsonConvert.SerializeObject(await SyncLoopProbe.Run(token), Formatting.Indented); ProbeStatus = "passed"; }
        catch (OperationCanceledException) { ProbeStatus = "cancelled"; }
        catch (Exception error) { ProbeStatus = "failed"; ProbeReport = error.ToString(); Log.LogError(error); }
        finally { _probeCancellation?.Dispose(); _probeCancellation = null; }
    }

    private async UniTaskVoid RunInteractionProbe(CancellationToken token)
    {
        try { ProbeReport = JsonConvert.SerializeObject(await InteractionPolicyProbe.Run(token), Formatting.Indented); ProbeStatus = "passed"; }
        catch (OperationCanceledException) { ProbeStatus = "cancelled"; }
        catch (Exception error) { ProbeStatus = "failed"; ProbeReport = error.ToString(); Log.LogError(error); }
        finally { _probeCancellation?.Dispose(); _probeCancellation = null; }
    }

    private async UniTaskVoid RunFlareProbe(CancellationToken token)
    {
        try { ProbeReport = JsonConvert.SerializeObject(await FlareExitProbe.Run(token), Formatting.Indented); ProbeStatus = "passed"; }
        catch (OperationCanceledException) { ProbeStatus = "cancelled"; }
        catch (Exception error) { ProbeStatus = "failed"; ProbeReport = error.ToString(); Log.LogError(error); }
        finally { _probeCancellation?.Dispose(); _probeCancellation = null; }
    }

    private async UniTaskVoid RunRenderProbe(CancellationToken token)
    {
        try { ProbeReport = JsonConvert.SerializeObject(await RenderServiceProbe.Run(token), Formatting.Indented); ProbeStatus = "passed"; }
        catch (OperationCanceledException) { ProbeStatus = "cancelled"; }
        catch (Exception error) { ProbeStatus = "failed"; ProbeReport = error.ToString(); Log.LogError(error); }
        finally { _probeCancellation?.Dispose(); _probeCancellation = null; }
    }

    public string BeginAudioProbe()
    {
        if (!AllowProbe.Value) throw new InvalidOperationException("Development probes are disabled");
        if (_probeCancellation != null) throw new InvalidOperationException("A development probe is already running");
        _probeCancellation = new CancellationTokenSource();
        ProbeStatus = "running";
        ProbeReport = "";
        RunAudioProbe(_probeCancellation.Token).Forget();
        return ProbeStatus;
    }

    private async UniTaskVoid RunAudioProbe(CancellationToken token)
    {
        try
        {
            ProbeReport = JsonConvert.SerializeObject(await AudioPolicyProbe.Run(token), Formatting.Indented);
            ProbeStatus = "passed";
        }
        catch (OperationCanceledException) { ProbeStatus = "cancelled"; }
        catch (Exception error) { ProbeStatus = "failed"; ProbeReport = error.ToString(); Log.LogError(error); }
        finally { _probeCancellation?.Dispose(); _probeCancellation = null; }
    }

    public string BeginTriggerProbe()
    {
        if (!AllowProbe.Value) throw new InvalidOperationException("Development probes are disabled");
        if (Singleton<GameWorld>.Instantiated) throw new InvalidOperationException("Run trigger validation from the main menu");
        if (_probeCancellation != null) throw new InvalidOperationException("A development probe is already running");
        _probeCancellation = new CancellationTokenSource();
        ProbeStatus = "running";
        ProbeReport = "";
        RunTriggerProbe(_probeCancellation.Token).Forget();
        return ProbeStatus;
    }

    private async UniTaskVoid RunTriggerProbe(CancellationToken token)
    {
        try
        {
            ProbeReport = JsonConvert.SerializeObject(await TriggerRelayProbe.Run(token), Formatting.Indented);
            ProbeStatus = "passed";
        }
        catch (OperationCanceledException) { ProbeStatus = "cancelled"; }
        catch (Exception error) { ProbeStatus = "failed"; ProbeReport = error.ToString(); Log.LogError(error); }
        finally { _probeCancellation?.Dispose(); _probeCancellation = null; }
    }

    public string BeginCloudProbe()
    {
        if (!AllowProbe.Value) throw new InvalidOperationException("Development probes are disabled");
        if (Singleton<GameWorld>.Instantiated) throw new InvalidOperationException("Run cloud validation from the main menu");
        if (_probeCancellation != null) throw new InvalidOperationException("A development probe is already running");
        _probeCancellation = new CancellationTokenSource();
        ProbeStatus = "running";
        ProbeReport = "";
        RunCloudProbe(_probeCancellation.Token).Forget();
        return ProbeStatus;
    }

    private async UniTaskVoid RunCloudProbe(CancellationToken token)
    {
        try
        {
            ProbeReport = JsonConvert.SerializeObject(await CloudLayerProbe.Run(token), Formatting.Indented);
            ProbeStatus = "passed";
        }
        catch (OperationCanceledException) { ProbeStatus = "cancelled"; }
        catch (Exception error) { ProbeStatus = "failed"; ProbeReport = error.ToString(); Log.LogError(error); }
        finally { _probeCancellation?.Dispose(); _probeCancellation = null; }
    }

    public string BeginBallisticProbe()
    {
        if (!AllowProbe.Value) throw new InvalidOperationException("Development probes are disabled");
        if (Singleton<GameWorld>.Instantiated) throw new InvalidOperationException("Run ballistic validation from the main menu");
        if (_probeCancellation != null) throw new InvalidOperationException("A development probe is already running");
        _probeCancellation = new CancellationTokenSource();
        ProbeStatus = "running";
        ProbeReport = "";
        RunBallisticProbe(_probeCancellation.Token).Forget();
        return ProbeStatus;
    }

    private async UniTaskVoid RunBallisticProbe(CancellationToken token)
    {
        try
        {
            ProbeReport = JsonConvert.SerializeObject(await BallisticTriggerProbe.Run(token), Formatting.Indented);
            ProbeStatus = "passed";
        }
        catch (OperationCanceledException) { ProbeStatus = "cancelled"; }
        catch (Exception error) { ProbeStatus = "failed"; ProbeReport = error.ToString(); Log.LogError(error); }
        finally { _probeCancellation?.Dispose(); _probeCancellation = null; }
    }

    public string BeginSeasonProbe()
    {
        if (!AllowProbe.Value) throw new InvalidOperationException("Development probes are disabled");
        if (Singleton<GameWorld>.Instantiated) throw new InvalidOperationException("Run seasonal validation from the main menu");
        if (_probeCancellation != null) throw new InvalidOperationException("A development probe is already running");
        _probeCancellation = new CancellationTokenSource();
        ProbeStatus = "running";
        ProbeReport = "";
        RunSeasonProbe(_probeCancellation.Token).Forget();
        return ProbeStatus;
    }

    private async UniTaskVoid RunSeasonProbe(CancellationToken token)
    {
        try
        {
            ProbeReport = JsonConvert.SerializeObject(await SeasonProbe.Run(token), Formatting.Indented);
            ProbeStatus = "passed";
        }
        catch (OperationCanceledException) { ProbeStatus = "cancelled"; }
        catch (Exception error) { ProbeStatus = "failed"; ProbeReport = error.ToString(); Log.LogError(error); }
        finally { _probeCancellation?.Dispose(); _probeCancellation = null; }
    }

    public string BeginAreaLightProbe()
    {
        if (!AllowProbe.Value) throw new InvalidOperationException("Development probes are disabled");
        if (Singleton<GameWorld>.Instantiated) throw new InvalidOperationException("Run area-light validation from the main menu");
        if (_probeCancellation != null) throw new InvalidOperationException("A development probe is already running");
        _probeCancellation = new CancellationTokenSource();
        ProbeStatus = "running";
        ProbeReport = "";
        RunAreaLightProbe(_probeCancellation.Token).Forget();
        return ProbeStatus;
    }

    private async UniTaskVoid RunAreaLightProbe(CancellationToken token)
    {
        try
        {
            ProbeReport = JsonConvert.SerializeObject(await AreaLightProbe.Run(token), Formatting.Indented);
            ProbeStatus = "passed";
        }
        catch (OperationCanceledException) { ProbeStatus = "cancelled"; }
        catch (Exception error) { ProbeStatus = "failed"; ProbeReport = error.ToString(); Log.LogError(error); }
        finally { _probeCancellation?.Dispose(); _probeCancellation = null; }
    }

    public string BeginNativeTextureProbe()
    {
        if (!AllowProbe.Value) throw new InvalidOperationException("Development probes are disabled");
        if (Singleton<GameWorld>.Instantiated) throw new InvalidOperationException("Run texture validation from the main menu");
        if (_probeCancellation != null) throw new InvalidOperationException("A development probe is already running");
        _probeCancellation = new CancellationTokenSource();
        ProbeStatus = "running";
        ProbeReport = "";
        RunNativeTextureProbe(_probeCancellation.Token).Forget();
        return ProbeStatus;
    }

    private async UniTaskVoid RunNativeTextureProbe(CancellationToken token)
    {
        try
        {
            ProbeReport = JsonConvert.SerializeObject(await NativeTextureProbe.Run(token), Formatting.Indented);
            ProbeStatus = "passed";
        }
        catch (OperationCanceledException) { ProbeStatus = "cancelled"; }
        catch (Exception error) { ProbeStatus = "failed"; ProbeReport = error.ToString(); Log.LogError(error); }
        finally { _probeCancellation?.Dispose(); _probeCancellation = null; }
    }

    public string BeginNativeReuseProbe()
    {
        if (!AllowProbe.Value) throw new InvalidOperationException("Development probes are disabled");
        if (Singleton<GameWorld>.Instantiated) throw new InvalidOperationException("Run native reuse validation from the main menu");
        if (_probeCancellation != null) throw new InvalidOperationException("A development probe is already running");
        _probeCancellation = new CancellationTokenSource();
        ProbeStatus = "running";
        ProbeReport = "";
        RunNativeReuseProbe(_probeCancellation.Token).Forget();
        return ProbeStatus;
    }

    private async UniTaskVoid RunNativeReuseProbe(CancellationToken token)
    {
        try
        {
            ProbeReport = JsonConvert.SerializeObject(await NativeReuseProbe.Run(token), Formatting.Indented);
            ProbeStatus = "passed";
        }
        catch (OperationCanceledException) { ProbeStatus = "cancelled"; }
        catch (Exception error) { ProbeStatus = "failed"; ProbeReport = error.ToString(); Log.LogError(error); }
        finally { _probeCancellation?.Dispose(); _probeCancellation = null; }
    }

    public string BeginRoomGeometryProbe()
    {
        if (!AllowProbe.Value) throw new InvalidOperationException("Development probes are disabled");
        if (Singleton<GameWorld>.Instantiated) throw new InvalidOperationException("Run room geometry validation from the main menu");
        if (_probeCancellation != null) throw new InvalidOperationException("A development probe is already running");
        _probeCancellation = new CancellationTokenSource();
        ProbeStatus = "running";
        ProbeReport = "";
        RunRoomGeometryProbe(_probeCancellation.Token).Forget();
        return ProbeStatus;
    }

    private async UniTaskVoid RunRoomGeometryProbe(CancellationToken token)
    {
        try
        {
            ProbeReport = JsonConvert.SerializeObject(await RoomGeometryProbe.Run(token), Formatting.Indented);
            ProbeStatus = "passed";
        }
        catch (OperationCanceledException) { ProbeStatus = "cancelled"; }
        catch (Exception error) { ProbeStatus = "failed"; ProbeReport = error.ToString(); Log.LogError(error); }
        finally { _probeCancellation?.Dispose(); _probeCancellation = null; }
    }

    private async UniTaskVoid RunProbe(CancellationToken token)
    {
        try
        {
            var report = await SceneLoader.RunProbe(token);
            ProbeReport = JsonConvert.SerializeObject(report, Formatting.Indented);
            ProbeStatus = "passed";
        }
        catch (OperationCanceledException) { ProbeStatus = "cancelled"; }
        catch (Exception error) { ProbeStatus = "failed"; ProbeReport = error.ToString(); Log.LogError(error); }
        finally { _probeCancellation?.Dispose(); _probeCancellation = null; }
    }

    private void OnDestroy()
    {
        _probeCancellation?.Cancel();
        SeasonBindings.Disable();
        TriggerRelayBindings.Disable();
        AudioBindings.Disable();
        AdvancedSoundBindings.Disable();
        SignalFlareBindings.Disable();
        FlareExitBindings.Disable();
        SyncLoopBindings.Disable();
        EnvironmentAudioBindings.Disable();
        AmbientBlendBindings.Disable();
        LevelLightingBindings.Disable();
        SceneManager.sceneUnloaded -= SceneLoader.SceneUnloaded;
        SceneManager.sceneUnloaded -= AreaLightBindings.SceneUnloaded;
    }
}
