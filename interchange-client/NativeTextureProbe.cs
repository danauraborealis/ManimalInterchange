using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Cysharp.Threading.Tasks;
using Manimal.Interchange.Shared;
using Newtonsoft.Json;
using UnityEngine;
using ZLinq;

namespace Manimal.Interchange.Client;

internal static class NativeTextureProbe
{
    private sealed class Input
    {
        public string Bundle = "";
        public string Sha256 = "";
        public Entry[] Assets = Array.Empty<Entry>();
    }
    private sealed class Entry
    {
        public string Path = "";
        public string Type = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public int Depth { get; set; }
        public int Format { get; set; }
        public int Mips { get; set; }
        public bool Readable { get; set; }
        public string ImageSha256 = "";
    }

    internal static async UniTask<object> Run(CancellationToken token)
    {
        var input = JsonConvert.DeserializeObject<Input>(File.ReadAllText(Path.Combine(Plugin.Root, "native-validation.json")))
            ?? throw new InvalidDataException("Native validation manifest missing");
        var path = ManifestRules.Resolve(Plugin.Root, input.Bundle);
        if (input.Assets.Length == 0 || ManifestRules.Hash(path) != input.Sha256) throw new InvalidDataException("Native validation inputs changed");
        AssetBundle? bundle = null;
        var records = new List<object>();
        try
        {
            token.ThrowIfCancellationRequested();
            var request = AssetBundle.LoadFromFileAsync(path);
            // Keep ownership until Unity finishes, even when cancellation arrives.
            await UniTask.WaitUntil(() => request.isDone);
            bundle = request.assetBundle;
            token.ThrowIfCancellationRequested();
            if (bundle == null) throw new InvalidDataException("Native validation bundle failed to load");
            if (bundle.GetAllAssetNames().Length != input.Assets.Length) throw new InvalidDataException("Native bundle asset count differs");
            foreach (var expected in input.Assets)
            {
                var load = bundle.LoadAssetAsync<Texture>(expected.Path);
                await UniTask.WaitUntil(() => load.isDone);
                token.ThrowIfCancellationRequested();
                var texture = load.asset as Texture;
                if (texture == null || texture.GetType().Name != expected.Type || texture.width != expected.Width ||
                    texture.height != expected.Height || texture.mipmapCount != expected.Mips)
                    throw new InvalidDataException("Runtime texture shape changed: " + expected.Path);
                string? imageHash = null;
                if (texture is Texture2D flat)
                {
                    if ((int)flat.format != expected.Format || flat.isReadable != expected.Readable)
                        throw new InvalidDataException("Runtime texture format/readability changed: " + expected.Path);
                    if (flat.isReadable)
                    {
                        using var sha = SHA256.Create();
                        imageHash = BitConverter.ToString(sha.ComputeHash(flat.GetRawTextureData())).Replace("-", "").ToLowerInvariant();
                        if (imageHash != expected.ImageSha256) throw new InvalidDataException("Runtime raw texture payload changed: " + expected.Path);
                    }
                }
                else if (texture is Texture2DArray array)
                {
                    if ((int)array.graphicsFormat != expected.Format || array.depth != expected.Depth || array.isReadable != expected.Readable)
                        throw new InvalidDataException("Runtime array format/layers/readability changed: " + expected.Path);
                }
                else throw new InvalidDataException("Unsupported texture probe class");
                if (texture.GetNativeTexturePtr() == IntPtr.Zero) throw new InvalidDataException("Texture has no native graphics allocation: " + expected.Path);
                records.Add(new { expected.Path, expected.Type, expected.Width, expected.Height, expected.Depth,
                    expected.Format, expected.Mips, expected.Readable, RawImageSha256 = imageHash, NativeAllocation = true });
            }
        }
        finally
        {
            if (bundle != null) bundle.Unload(true);
        }
        if (AssetBundle.GetAllLoadedAssetBundles().AsValueEnumerable().Any(item => ReferenceEquals(item, bundle)))
            throw new InvalidOperationException("Validation bundle remained loaded after cleanup");
        return new { Passed = true, BundleSha256 = input.Sha256, Assets = records, OwnedBundleReleased = true };
    }
}
