using System.Text.Json;
using JetBrains.Annotations;
using Manimal.Interchange.Shared;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils;

namespace Manimal.Interchange.Server;

[Injectable(TypePriority = OnLoadOrder.Routers + 1), UsedImplicitly]
public sealed class InterchangeCapabilityRouter(JsonUtil jsonUtil) : StaticRouter(jsonUtil,
    [new RouteAction<EmptyRequestData>(ManifestRules.CapabilityRoute, (_, _, _, _, _) => new ValueTask<string>(ReadCapability()))])
{
    private static readonly JsonSerializerOptions Options = new() { IncludeFields = true };

    public static string ReadCapability()
    {
        var result = new ServerCapability();

        try
        {
            var root = Path.GetDirectoryName(typeof(InterchangeCapabilityRouter).Assembly.Location)!;
            var path = Path.Combine(root, ManifestRules.FileName);
            if (!File.Exists(path))
            {
                return JsonSerializer.Serialize(result, Options);
            }

            var manifest = JsonSerializer.Deserialize<ContentManifest>(File.ReadAllText(path), Options)
                ?? throw new InvalidDataException("Null Interchange content manifest");
            ManifestRules.Validate(manifest);

            // The route verifies the server-side declarations on every read so
            // a deleted or replaced payload cannot leave an old ready bit in
            // the handshake.  Client bundles are deliberately not checked
            // here: they are rooted at the client plugin and are checked by
            // SceneLoader.
            foreach (var file in manifest.ServerFiles)
            {
                ManifestRules.VerifyFile(root, file);
            }

            foreach (var file in manifest.ItemFiles)
            {
                ManifestRules.VerifyFile(root, file);
            }

            var currentHash = ManifestRules.Hash(path);
            result.ContentId = manifest.ContentId;
            result.ManifestSha256 = currentHash;

            if (manifest.Mode == "probe")
            {
                result.Selection = "probe";
                result.Ready = InterchangeServerState.IsProbeReady(manifest.ContentId, currentHash);
            }
            else if (manifest.Mode is "test" or "rework")
            {
                result.Selection = "rework";
                result.Ready = InterchangeServerState.IsFullReady(manifest.ContentId, manifest.Mode, currentHash);
            }

            if (!result.Ready)
            {
                var currentState = InterchangeServerState.Snapshot();
                var sameIdentity = currentState.ContentId == manifest.ContentId
                    && currentState.Mode == manifest.Mode
                    && currentState.StartupManifestSha256 == currentHash;
                result.Error = !sameIdentity || string.IsNullOrWhiteSpace(currentState.Error)
                    ? "Server content was not applied for the current manifest identity."
                    : currentState.Error;
            }
        }
        catch (Exception error) { result.Ready = false; result.Error = error.Message; }
        return JsonSerializer.Serialize(result, Options);
    }
}
