using System.Reflection;
using System.Text.Json;
using JetBrains.Annotations;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Routers;

namespace Manimal.Interchange.Server;

// The retail base declares six loading-screen banners. SPT 4.1.5 ships the
// images and none of the captions for four of them, and nothing at all for the
// two 1.x additions, so the client logs unhandled /files/banners requests and
// the loading screen shows no text. The two images and the English captions
// travel inside this assembly; the locale fragment contract stays at its
// audited key count.
[Injectable(InjectionType = InjectionType.Singleton, TypePriority = OnLoadOrder.Preload + 6), UsedImplicitly]
public sealed class InterchangeBannerCompatibility(
    LocationTable locations,
    LocaleTable locales,
    ImageRouter imageRouter,
    ISptLogger<InterchangeBannerCompatibility> logger) : IOnLoad
{
    private const string ResourcePrefix = "Manimal.Interchange.Banners.";
    private const string CaptionsResource = ResourcePrefix + "captions-en.json";

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!InterchangeServerState.Ready) return Task.CompletedTask;

        var assembly = typeof(InterchangeBannerCompatibility).Assembly;
        var root = Path.GetDirectoryName(assembly.Location)!;
        try
        {
            RouteImages(assembly, root);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.Warning("Interchange: banner image routing failed; those loading screens fall back to default art: " + error.Message);
        }
        try
        {
            AddCaptions(assembly);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.Warning("Interchange: banner captions failed: " + error.Message);
        }
        return Task.CompletedTask;
    }

    // ImageRouter wants a file on disk; unpack the embedded copies next to the
    // dll once and point the route there. SPT already serves the four images it
    // ships itself, so only files we embed get a route.
    private void RouteImages(Assembly assembly, string root)
    {
        var banners = locations.Interchange?.Base?.Banners;
        if (banners is null || banners.Count == 0) return;
        var dir = Path.Combine(root, "banners");
        var routed = 0;
        foreach (var banner in banners)
        {
            var file = banner?.Picture?.File;
            if (string.IsNullOrEmpty(file)) continue;
            using var stream = assembly.GetManifestResourceStream(ResourcePrefix + file);
            if (stream is null) continue;
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, file);
            if (!File.Exists(target) || new FileInfo(target).Length != stream.Length)
            {
                using var output = File.Create(target);
                stream.CopyTo(output);
            }
            imageRouter.AddRoute("/files/banners/" + Path.GetFileNameWithoutExtension(file), target);
            routed++;
        }
        logger.Info("Interchange: " + routed + " retail loading-screen banner image(s) routed.");
    }

    // captions are keyed "<bannerId> Name" / "<bannerId> Description"; add them
    // to every language without overwriting anything a locale already has
    private void AddCaptions(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream(CaptionsResource)
            ?? throw new InvalidDataException("Embedded banner captions are missing.");
        var captions = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidDataException("Embedded banner captions are empty.");
        var languages = locales.Global;
        if (languages is null) return;
        foreach (var pair in languages)
        {
            pair.Value?.AddTransformer(locale =>
            {
                if (locale is not null) foreach (var caption in captions) locale.TryAdd(caption.Key, caption.Value);
                return locale;
            });
        }
        logger.Info("Interchange: " + captions.Count / 2 + " loading-screen banner caption(s) added to " + languages.Count + " language(s).");
    }
}
