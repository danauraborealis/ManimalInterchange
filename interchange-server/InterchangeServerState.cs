using Manimal.Interchange.Shared;

namespace Manimal.Interchange.Server;

/// <summary>
/// Holds the startup identity which the capability route is allowed to
/// advertise.  A valid manifest is not sufficient: the full-map registration
/// must finish, and the manifest hash observed by the route must still equal
/// the hash captured at startup.
/// </summary>
public static class InterchangeServerState
{
    private static readonly object Sync = new();
    private static StateSnapshot _snapshot = StateSnapshot.Empty;

    public static StateSnapshot Snapshot()
    {
        lock (Sync)
        {
            return _snapshot;
        }
    }

    // Read-only aliases keep the startup contract easy to inspect from a
    // fixture or a diagnostics route without exposing mutable state.
    public static string StartupManifestHash => Snapshot().StartupManifestSha256;
    public static string AppliedManifestHash => Snapshot().AppliedManifestSha256;
    public static bool Ready => Snapshot().Ready;

    public static bool Allows(ContentManifest manifest, string root)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(root);

        if (manifest.Mode == "rework" && manifest.Ready)
        {
            return true;
        }

        // Test content is deliberately opt-in.  ManifestRules disallows a
        // test manifest from claiming release readiness, so the sentinel is
        // an explicit local-development gate for the full-map registration.
        return manifest.Mode == "test"
            && !manifest.Ready
            && File.Exists(Path.Combine(root, "allow-fullmap-test"));
    }

    internal static void BeginStartup()
    {
        lock (Sync)
        {
            _snapshot = StateSnapshot.Empty;
        }
    }

    internal static void MarkInactive(string contentId, string mode, string manifestSha256, string reason)
    {
        lock (Sync)
        {
            _snapshot = new StateSnapshot(
                contentId,
                mode,
                "native",
                manifestSha256,
                "",
                false,
                false,
                reason);
        }
    }

    internal static void MarkProbe(string contentId, string manifestSha256, bool allowed)
    {
        lock (Sync)
        {
            _snapshot = new StateSnapshot(
                contentId,
                "probe",
                "probe",
                manifestSha256,
                "",
                false,
                allowed,
                allowed ? "" : "Probe mode requires the allow-probe sentinel.");
        }
    }

    internal static void MarkApplied(ContentManifest manifest, string manifestSha256)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        lock (Sync)
        {
            _snapshot = new StateSnapshot(
                manifest.ContentId,
                manifest.Mode,
                "rework",
                manifestSha256,
                manifestSha256,
                true,
                false,
                "");
        }
    }

    internal static void MarkFailure(string contentId, string mode, string manifestSha256, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        lock (Sync)
        {
            _snapshot = new StateSnapshot(
                contentId,
                mode,
                "native",
                manifestSha256,
                "",
                false,
                false,
                error.Message);
        }
    }

    internal static void Clear()
    {
        lock (Sync)
        {
            _snapshot = StateSnapshot.Empty;
        }
    }

    public static bool IsFullReady(string contentId, string mode, string currentManifestSha256)
    {
        lock (Sync)
        {
            return _snapshot.Ready
                && !_snapshot.ProbeReady
                && _snapshot.ContentId == contentId
                && _snapshot.Mode == mode
                && _snapshot.StartupManifestSha256 == currentManifestSha256
                && _snapshot.AppliedManifestSha256 == currentManifestSha256;
        }
    }

    public static bool IsProbeReady(string contentId, string currentManifestSha256)
    {
        lock (Sync)
        {
            return _snapshot.ProbeReady
                && _snapshot.ContentId == contentId
                && _snapshot.Mode == "probe"
                && _snapshot.StartupManifestSha256 == currentManifestSha256;
        }
    }

    public sealed record StateSnapshot(
        string ContentId,
        string Mode,
        string Selection,
        string StartupManifestSha256,
        string AppliedManifestSha256,
        bool Ready,
        bool ProbeReady,
        string Error)
    {
        public static StateSnapshot Empty { get; } = new("", "", "native", "", "", false, false, "");
    }
}
