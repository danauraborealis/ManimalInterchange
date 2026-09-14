using System;
using UnityEngine;
using ZLinq;

namespace Manimal.Interchange.Components;

[Serializable]
public struct RoomBox
{
    public Vector3 WorldCenter;
    public Quaternion InverseRotation;
    public Vector3 HalfSize;

    public bool ContainsPoint(Vector3 worldPoint)
    {
        // Verified against the pinned retail ContainsPoint native instructions:
        // subtract center, apply inverse quaternion, then inclusive axis tests.
        var local = InverseRotation * (worldPoint - WorldCenter);
        return Mathf.Abs(local.x) <= HalfSize.x && Mathf.Abs(local.y) <= HalfSize.y && Mathf.Abs(local.z) <= HalfSize.z;
    }
}

[DisallowMultipleComponent]
public sealed class RoomGeometry : MonoBehaviour
{
    public string SourceKey = "";
    public int SourceRoomId;
    public RoomBox[] Boxes = Array.Empty<RoomBox>();

    // Keep ZLinq's array predicate allocation-free after the first query on
    // each thread. Restoring the point also supports nested queries.
    [ThreadStatic] private static BoxQuery? _query;
    private sealed class BoxQuery
    {
        internal Vector3 Point;
        internal readonly Func<RoomBox, bool> Predicate;
        internal BoxQuery() => Predicate = box => box.ContainsPoint(Point);
    }

    public bool ContainsPoint(Vector3 point)
    {
        var query = _query ??= new BoxQuery();
        var previous = query.Point;
        query.Point = point;
        try { return Boxes.AsValueEnumerable().Any(query.Predicate); }
        finally { query.Point = previous; }
    }
}
