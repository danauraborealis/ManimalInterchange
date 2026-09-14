using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Manimal.Interchange.Shared;
using Newtonsoft.Json;
using UnityEngine;
using ZLinq;

namespace Manimal.Interchange.Client;

internal static class NativeReuseProbe
{
    private sealed class Input
    {
        public string Bundle = "";
        public string Sha256 = "";
        public Entry[] Assets = Array.Empty<Entry>();
        public Donor[] DonorFiles = Array.Empty<Donor>();
    }
    private sealed class Donor
    {
        public string File = "";
        public long Bytes { get; set; }
        public string Sha256 = "";
    }
    private sealed class Entry
    {
        public string Path = "";
        public string Type = "";
        public string Name = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public int Depth { get; set; }
        public int Format { get; set; }
        public int Mips { get; set; }
        public bool Readable { get; set; }
        public int Vertices { get; set; }
        public int SubMeshes { get; set; }
        public int Channels { get; set; }
        public int Frequency { get; set; }
        public float Seconds { get; set; }
        public int LoadType { get; set; }
    }

    internal static async UniTask<object> Run(CancellationToken token)
    {
        var input = JsonConvert.DeserializeObject<Input>(File.ReadAllText(Path.Combine(Plugin.Root, "native-reuse-validation.json")))
            ?? throw new InvalidDataException("Native reuse manifest missing");
        var path = ManifestRules.Resolve(Plugin.Root, input.Bundle);
        var dataRoot = Application.dataPath;
        if (input.Assets.Length == 0 || input.DonorFiles.Length == 0) throw new InvalidDataException("Empty native reuse manifest");
        await UniTask.RunOnThreadPool(() =>
        {
            if (ManifestRules.Hash(path) != input.Sha256) throw new InvalidDataException("Native reuse fixture changed");
            foreach (var donor in input.DonorFiles)
            {
                token.ThrowIfCancellationRequested();
                var source = ManifestRules.Resolve(dataRoot, donor.File);
                if (new FileInfo(source).Length != donor.Bytes || ManifestRules.Hash(source) != donor.Sha256)
                    throw new InvalidDataException("Installed native donor changed: " + donor.File);
            }
        });
        await UniTask.SwitchToMainThread();
        AssetBundle? bundle = null;
        var retained = new List<UnityEngine.Object>();
        var records = new List<object>();
        var errors = new List<string>();
        var counts = new Dictionary<string, int>();
        void OnLog(string message, string stack, LogType kind)
        { if (kind is LogType.Error or LogType.Exception or LogType.Assert) errors.Add(message + "\n" + stack); }
        Application.logMessageReceived += OnLog;
        try
        {
            token.ThrowIfCancellationRequested();
            var request = AssetBundle.LoadFromFileAsync(path);
            await UniTask.WaitUntil(() => request.isDone);
            bundle = request.assetBundle;
            token.ThrowIfCancellationRequested();
            if (bundle == null || bundle.GetAllAssetNames().Length != input.Assets.Length)
                throw new InvalidDataException("Native reuse bundle failed or root count differs");
            foreach (var expected in input.Assets)
            {
                var load = bundle.LoadAssetAsync<UnityEngine.Object>(expected.Path);
                await UniTask.WaitUntil(() => load.isDone);
                token.ThrowIfCancellationRequested();
                var asset = load.asset;
                if (asset == null || asset.GetType().Name != expected.Type || asset.name != expected.Name)
                    throw new InvalidDataException("Native donor identity differs: " + expected.Path);
                retained.Add(asset);
                switch (asset)
                {
                    case Texture2D texture:
                        if (texture.width != expected.Width || texture.height != expected.Height || (int)texture.format != expected.Format ||
                            texture.mipmapCount != expected.Mips || texture.isReadable != expected.Readable || texture.GetNativeTexturePtr() == IntPtr.Zero)
                            throw new InvalidDataException("Native texture properties/allocation differ: " + expected.Path);
                        break;
                    case Mesh mesh:
                        if (mesh.vertexCount != expected.Vertices || mesh.subMeshCount != expected.SubMeshes ||
                            (expected.Vertices > 0 && mesh.GetNativeVertexBufferPtr(0) == IntPtr.Zero))
                            throw new InvalidDataException("Native mesh properties/allocation differ: " + expected.Path);
                        break;
                    case Cubemap cube:
                        if (cube.width != expected.Width || cube.height != expected.Height || (int)cube.format != expected.Format ||
                            cube.mipmapCount != expected.Mips || cube.isReadable != expected.Readable || cube.GetNativeTexturePtr() == IntPtr.Zero)
                            throw new InvalidDataException("Native cubemap properties/allocation differ: " + expected.Path);
                        break;
                    case Texture2DArray array:
                        if (array.width != expected.Width || array.height != expected.Height || array.depth != expected.Depth ||
                            (int)array.graphicsFormat != expected.Format || array.mipmapCount != expected.Mips ||
                            array.isReadable != expected.Readable || array.GetNativeTexturePtr() == IntPtr.Zero)
                            throw new InvalidDataException("Native texture array properties/allocation differ: " + expected.Path);
                        break;
                    case AudioClip clip:
                        if (clip.channels != expected.Channels || clip.frequency != expected.Frequency || Math.Abs(clip.length - expected.Seconds) > 0.001f ||
                            (int)clip.loadType != expected.LoadType)
                            throw new InvalidDataException("Native audio properties differ: " + expected.Path);
                        if (!clip.LoadAudioData()) throw new InvalidDataException("Native audio data request failed: " + expected.Path);
                        var deadline = Time.realtimeSinceStartup + 30f;
                        await UniTask.WaitUntil(() => clip.loadState != AudioDataLoadState.Loading || Time.realtimeSinceStartup >= deadline);
                        token.ThrowIfCancellationRequested();
                        if (clip.loadState != AudioDataLoadState.Loaded) throw new InvalidDataException("Native audio data failed to load: " + expected.Path);
                        break;
                    default: throw new InvalidDataException("Unsupported native reuse class");
                }
                records.Add(new { expected.Path, expected.Type, Name = asset.name, InstanceId = asset.GetInstanceID(), PropertiesAndAllocationPassed = true });
                counts.TryGetValue(expected.Type, out var count); counts[expected.Type] = count + 1;
            }
        }
        finally
        {
            // The container owns no donor objects. Never destroy objects owned
            // by the player's native serialized files during probe cleanup.
            try
            {
                if (bundle != null) bundle.Unload(false);
                if (AssetBundle.GetAllLoadedAssetBundles().AsValueEnumerable().Any(item => ReferenceEquals(item, bundle)))
                    throw new InvalidOperationException("Native reuse container remained loaded");
            }
            finally { Application.logMessageReceived -= OnLog; }
        }
        if (errors.Count != 0) throw new InvalidDataException("Native donor load errors: " + string.Join("\n", errors));
        foreach (var asset in retained)
            if (asset == null) throw new InvalidOperationException("Native donor was destroyed with probe container");
        if (records.Count > 500)
        {
            using var hasher = SHA256.Create();
            var hash = BitConverter.ToString(hasher.ComputeHash(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(records)))).Replace("-", "").ToLowerInvariant();
            return new { Passed = true, BundleSha256 = input.Sha256, DonorFilesVerified = input.DonorFiles.Length,
                AssetsChecked = records.Count, Types = counts, InventoryAndInstanceSha256 = hash, Errors = errors,
                OwnedBundleReleased = true, NativeDonorsRetained = true };
        }
        return new { Passed = true, BundleSha256 = input.Sha256, DonorFilesVerified = input.DonorFiles.Length,
            Assets = records, Errors = errors, OwnedBundleReleased = true, NativeDonorsRetained = true };
    }
}
