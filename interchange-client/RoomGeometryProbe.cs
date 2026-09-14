using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Manimal.Interchange.Components;
using Manimal.Interchange.Shared;
using Newtonsoft.Json;
using UnityEngine;
using ZLinq;

namespace Manimal.Interchange.Client;

internal static class RoomGeometryProbe
{
    private sealed class Input
    {
        public string Bundle = "";
        public string Sha256 = "";
        public string Prefab = "";
    }

    internal static async UniTask<object> Run(CancellationToken token)
    {
        var input = JsonConvert.DeserializeObject<Input>(File.ReadAllText(Path.Combine(Plugin.Root, "room-geometry-validation.json")))
            ?? throw new InvalidDataException("Room geometry probe manifest missing");
        var path = ManifestRules.Resolve(Plugin.Root, input.Bundle);
        if (ManifestRules.Hash(path) != input.Sha256) throw new InvalidDataException("Room geometry fixture changed");
        AssetBundle? bundle = null;
        GameObject? roomObject = null;
        var colliderObjects = new List<GameObject>();
        var checks = new List<object>();
        long? allocatedBytes = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var request = AssetBundle.LoadFromFileAsync(path);
            await UniTask.WaitUntil(() => request.isDone);
            bundle = request.assetBundle;
            token.ThrowIfCancellationRequested();
            if (bundle == null) throw new InvalidDataException("Room geometry bundle did not load");
            var load = bundle.LoadAssetAsync<GameObject>(input.Prefab);
            await UniTask.WaitUntil(() => load.isDone);
            token.ThrowIfCancellationRequested();
            var prefab = load.asset as GameObject;
            if (prefab == null || prefab.activeSelf) throw new InvalidDataException("Room fixture must remain inactive to avoid audio lifecycle side effects");
            roomObject = UnityEngine.Object.Instantiate(prefab);
            var geometry = roomObject.GetComponent<RoomGeometry>();
            if (!geometry || geometry.Boxes.Length != 2 || geometry.SourceKey != "probe/rotated-room")
                throw new InvalidDataException("Room geometry component import/binding failed");
            var room = roomObject.AddComponent<SpatialAudioRoom>();
            var colliders = new List<BoxCollider>();
            foreach (var box in geometry.Boxes)
            {
                var go = new GameObject("MI_RoomGeometryColliderProbe");
                colliderObjects.Add(go);
                go.transform.SetPositionAndRotation(box.WorldCenter, Quaternion.Inverse(box.InverseRotation));
                var collider = go.AddComponent<BoxCollider>();
                collider.isTrigger = true;
                collider.size = box.HalfSize * 2f;
                colliders.Add(collider);
            }
            Physics.SyncTransforms();
            var bounds = colliders[0].bounds;
            foreach (var collider in colliders) bounds.Encapsulate(collider.bounds);
            room.Colliders = colliders.ToArray();
            AccessTools.Field(typeof(SpatialAudioRoom), "_bounds").SetValue(room, bounds);
            AccessTools.Field(typeof(SpatialAudioRoom), "_iD").SetValue(room, checked((short)geometry.SourceRoomId));
            var method = RoomGeometryPatch.QueryMethod;
            // This verified target method reads only its room/point arguments.
            // Avoid constructing a second live audio storage or subscribing it.
            var storage = FormatterServices.GetUninitializedObject(method.DeclaringType!);
            bool Query(Vector3 point) => (bool)method.Invoke(storage, new object[] { room, point })!;
            var center = geometry.Boxes[0].WorldCenter;
            var corner = center + new Vector3(1.3f, 0f, 1.3f);
            geometry.enabled = false;
            if (!Query(corner)) throw new InvalidDataException("Fixture does not reproduce the native AABB false positive");
            checks.Add(new { Name = "native AABB false-positive reproduced", Passed = true });
            geometry.enabled = true;
            void Check(string name, Vector3 point, bool expected)
            {
                token.ThrowIfCancellationRequested();
                var actual = Query(point);
                if (actual != expected) throw new InvalidDataException("Patched room containment differs: " + name);
                checks.Add(new { Name = name, Passed = true, Expected = expected, Actual = actual });
            }
            Check("rotated room center", center, true);
            Check("rotated room interior", center + Quaternion.Inverse(geometry.Boxes[0].InverseRotation) * new Vector3(1.5f, .2f, .1f), true);
            Check("rotated AABB corner excluded", corner, false);
            Check("above rotated room excluded", center + Vector3.up * 1.01f, false);
            var second = geometry.Boxes[1];
            Check("second area center", second.WorldCenter, true);
            Check("axis-aligned face inclusive", second.WorldCenter + Vector3.right * second.HalfSize.x, true);
            Check("outside axis-aligned face", second.WorldCenter + Vector3.right * (second.HalfSize.x + .01f), false);
            Check("outside all areas", center + Vector3.forward * 20f, false);
            geometry.SourceRoomId++;
            try { Query(center); throw new InvalidDataException("Room identity mismatch was accepted"); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException)
            { checks.Add(new { Name = "room identity mismatch rejected", Passed = true }); }
            finally { geometry.SourceRoomId--; }
            var allocationMethod = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
            if (allocationMethod != null)
            {
                var counter = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), allocationMethod);
                geometry.ContainsPoint(center);
                var before = counter();
                for (var i = 0; i < 20000; i++)
                    if (!geometry.ContainsPoint(center)) throw new InvalidOperationException("Repeated room query changed");
                allocatedBytes = counter() - before;
                if (allocatedBytes > 1024) throw new InvalidOperationException("Room containment allocates per query");
            }
        }
        finally
        {
            if (roomObject) UnityEngine.Object.Destroy(roomObject);
            foreach (var go in colliderObjects) if (go) UnityEngine.Object.Destroy(go);
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
            if (bundle != null) bundle.Unload(true);
        }
        if (roomObject || colliderObjects.AsValueEnumerable().Any(go => go != null) ||
            AssetBundle.GetAllLoadedAssetBundles().AsValueEnumerable().Any(item => ReferenceEquals(item, bundle)))
            throw new InvalidOperationException("Room geometry probe cleanup failed");
        return new { Passed = true, BundleSha256 = input.Sha256, Checks = checks, WarmQueryCount = 20000,
            WarmQueryAllocatedBytes = allocatedBytes, OwnedBundleReleased = true, TemporaryObjectsDestroyed = true };
    }
}
