"""Replace the runtime NavMeshData in a Unity asset bundle.

The source is an imported Unity 2022.3 NavMeshData asset and the destination is
an existing AssetBundle containing the runtime NavMeshData.  This tool keeps
the bundle's object identities, serialized external references, and resource
streams intact while projecting the source fields onto the runtime type tree.
It always writes a separate output bundle; the inputs are never modified.

Run this with the project's UnityPy 1.25.3 audit runtime, for example through
``tools/run-python.ps1``.  The script intentionally has no imports from the
repository's ignored tooling so that this file is reproducible on its own.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
from pathlib import Path
from typing import Any, Iterable, Iterator

import UnityPy
from UnityPy.streams import EndianBinaryReader


EXPECTED_UNITY_VERSION = "2022.3.43f1"
EXPECTED_NAV_ASSETS_FILE = "CAB-60fc02272bc9ca066097b8f5410b7513"
EXPECTED_NAV_PATH_ID = 8071603940164206356


class IntegrationError(ValueError):
    """A failed preflight or post-write integrity check."""


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _children(file: Any) -> Iterable[tuple[str, Any]]:
    files = getattr(file, "files", None)
    if files is None:
        return ()
    return files.items()


def walk_files(file: Any, path: tuple[str, ...] = ()) -> Iterator[tuple[tuple[str, ...], Any]]:
    """Yield every nested UnityPy file with a stable path."""

    yield path, file
    for name, child in _children(file):
        yield from walk_files(child, path + (str(name),))


def serialized_files(environment: Any) -> Iterator[Any]:
    for top_name, top_file in environment.files.items():
        for _, file in walk_files(top_file, (str(top_name),)):
            if hasattr(file, "objects") and hasattr(file, "externals"):
                yield file


def resource_hashes(environment: Any) -> dict[str, str]:
    """Hash raw resource nodes while excluding SerializedFile object data."""

    result: dict[str, str] = {}
    for _, top_file in environment.files.items():
        # The top-level BundleFile name is the physical input path in UnityPy
        # and therefore changes when --output uses a different filename.  A
        # nested resource name is the stable identity we need to compare.
        for path, file in walk_files(top_file, ()):
            if hasattr(file, "objects"):
                continue
            if hasattr(file, "files"):
                continue
            if isinstance(file, EndianBinaryReader):
                result["/".join(path)] = hashlib.sha256(file.bytes).hexdigest()
                continue
            raise IntegrationError(f"Unverified bundle payload type at {'/'.join(path)}: {type(file)!r}")
    return result


def object_key(obj: Any) -> tuple[str, int, str]:
    return (str(obj.assets_file.name), int(obj.path_id), str(obj.type.name))


def object_hashes(environment: Any) -> dict[tuple[str, int, str], str]:
    result: dict[tuple[str, int, str], str] = {}
    for obj in environment.objects:
        key = object_key(obj)
        if key in result:
            raise IntegrationError(f"Duplicate serialized object identity: {key}")
        result[key] = hashlib.sha256(obj.get_raw_data()).hexdigest()
    return result


def _bytes_hex(value: Any) -> str:
    if isinstance(value, (bytes, bytearray, memoryview)):
        return bytes(value).hex()
    return repr(value)


def external_reference_signatures(environment: Any) -> dict[str, list[dict[str, Any]]]:
    result: dict[str, list[dict[str, Any]]] = {}
    for serialized in serialized_files(environment):
        refs: list[dict[str, Any]] = []
        for external in serialized.externals or []:
            refs.append(
                {
                    "name": str(getattr(external, "name", "")),
                    "path": str(getattr(external, "path", "")),
                    "guid": _bytes_hex(getattr(external, "guid", b"")),
                    "type": int(getattr(external, "type", 0)),
                    "temp_empty": bool(getattr(external, "temp_empty", False)),
                }
            )
        result[str(serialized.name)] = refs
    return result


def _path_text(path: tuple[Any, ...]) -> str:
    return "$" + "".join(f"/{part}" for part in path)


def nonzero_pointers(value: Any, path: tuple[Any, ...] = ()) -> list[str]:
    """Find serialized PPtr-shaped dictionaries with a non-null identity."""

    found: list[str] = []
    if isinstance(value, dict):
        if "m_FileID" in value and "m_PathID" in value:
            try:
                file_id = int(value["m_FileID"])
                path_id = int(value["m_PathID"])
            except (TypeError, ValueError) as exc:
                raise IntegrationError(f"Invalid object pointer at {_path_text(path)}") from exc
            if file_id != 0 or path_id != 0:
                found.append(_path_text(path))
        for key, child in value.items():
            found.extend(nonzero_pointers(child, path + (key,)))
    elif isinstance(value, (list, tuple)):
        for index, child in enumerate(value):
            found.extend(nonzero_pointers(child, path + (index,)))
    return found


def normalize_zero(value: Any) -> Any:
    if isinstance(value, float) and value == 0.0:
        return 0.0
    if isinstance(value, dict):
        return {key: normalize_zero(child) for key, child in value.items()}
    if isinstance(value, list):
        return [normalize_zero(child) for child in value]
    if isinstance(value, tuple):
        return tuple(normalize_zero(child) for child in value)
    return value


def values_equal(left: Any, right: Any, path: tuple[Any, ...] = ()) -> bool:
    """Strict typetree equality with signed zero treated as the same value."""

    if isinstance(left, float) and isinstance(right, float):
        return left == right
    if type(left) is not type(right):
        return False
    if isinstance(left, dict):
        return set(left) == set(right) and all(
            values_equal(left[key], right[key], path + (key,)) for key in left
        )
    if isinstance(left, (list, tuple)):
        return len(left) == len(right) and all(
            values_equal(a, b, path + (index,)) for index, (a, b) in enumerate(zip(left, right))
        )
    return left == right


def field_differences(expected: dict[str, Any], actual: dict[str, Any]) -> list[str]:
    differences: list[str] = []
    if set(expected) != set(actual):
        differences.append(
            f"field set differs (missing={sorted(set(expected) - set(actual))}, "
            f"extra={sorted(set(actual) - set(expected))})"
        )
    for key in sorted(set(expected) & set(actual)):
        if not values_equal(expected[key], actual[key], (key,)):
            differences.append(key)
    return differences


def tile_summary(tree: dict[str, Any]) -> dict[str, Any]:
    tiles = tree.get("m_NavMeshTiles")
    if not isinstance(tiles, list) or not tiles:
        raise IntegrationError("NavMeshData m_NavMeshTiles must be a non-empty list")
    lengths: list[int] = []
    for index, tile in enumerate(tiles):
        if not isinstance(tile, dict) or "m_MeshData" not in tile:
            raise IntegrationError(f"NavMesh tile {index} has no m_MeshData field")
        data = tile["m_MeshData"]
        if not isinstance(data, (bytes, bytearray, list, tuple)) or not data:
            raise IntegrationError(f"NavMesh tile {index} has empty m_MeshData")
        lengths.append(len(data))
    return {
        "count": len(tiles),
        "nonempty_count": sum(length > 0 for length in lengths),
        "mesh_data_bytes": sum(lengths),
        "min_mesh_data_bytes": min(lengths),
        "max_mesh_data_bytes": max(lengths),
        "bounds": tree.get("m_SourceBounds"),
    }


def nav_objects(environment: Any) -> list[Any]:
    return [obj for obj in environment.objects if obj.type.name == "NavMeshData"]


def require_single_nav(environment: Any, label: str) -> Any:
    objects = nav_objects(environment)
    if len(objects) != 1:
        raise IntegrationError(f"{label} must contain exactly one NavMeshData object; found {len(objects)}")
    obj = objects[0]
    if obj.assets_file.unity_version != EXPECTED_UNITY_VERSION:
        raise IntegrationError(
            f"{label} Unity version must be {EXPECTED_UNITY_VERSION}; "
            f"found {obj.assets_file.unity_version}"
        )
    return obj


def _resolved(path: Path) -> str:
    return os.path.normcase(os.path.abspath(str(path)))


def verify_output(
    output: Path,
    source_tree: dict[str, Any],
    target_field_set: set[str],
    target_name: str,
    expected_objects: dict[tuple[str, int, str], str],
    expected_externals: dict[str, list[dict[str, Any]]],
    expected_resources: dict[str, str],
    expected_nav_key: tuple[str, int, str],
) -> dict[str, Any]:
    output_environment = UnityPy.load(str(output))
    output_nav = require_single_nav(output_environment, "Output bundle")
    if output_nav.assets_file.name != EXPECTED_NAV_ASSETS_FILE:
        raise IntegrationError(
            f"Output NavMeshData serialized file changed: expected {EXPECTED_NAV_ASSETS_FILE}, "
            f"found {output_nav.assets_file.name}"
        )
    if int(output_nav.path_id) != EXPECTED_NAV_PATH_ID:
        raise IntegrationError(
            f"Output NavMeshData path ID changed: expected {EXPECTED_NAV_PATH_ID}, "
            f"found {output_nav.path_id}"
        )

    output_tree = output_nav.read_typetree()
    if set(output_tree) != target_field_set:
        raise IntegrationError(
            "Output NavMeshData field set changed: "
            f"expected {sorted(target_field_set)}, found {sorted(output_tree)}"
        )
    expected_tree = {key: source_tree[key] for key in target_field_set}
    expected_tree["m_Name"] = target_name
    differences = field_differences(expected_tree, output_tree)
    if differences:
        raise IntegrationError(f"Output NavMeshData fields differ: {differences[:8]}")
    output_pointers = nonzero_pointers(output_tree, ("NavMeshData", int(output_nav.path_id)))
    if output_pointers:
        raise IntegrationError(f"Output contains nonzero object pointers: {output_pointers[:8]}")

    actual_objects = object_hashes(output_environment)
    if set(actual_objects) != set(expected_objects):
        raise IntegrationError(
            f"Output serialized object set changed: expected {len(expected_objects)}, "
            f"found {len(actual_objects)}"
        )
    unrelated_changed = [
        key for key in expected_objects if key != expected_nav_key and actual_objects[key] != expected_objects[key]
    ]
    if unrelated_changed:
        raise IntegrationError(f"Unrelated serialized objects changed: {unrelated_changed[:8]}")
    if actual_objects[expected_nav_key] == expected_objects[expected_nav_key]:
        raise IntegrationError("NavMeshData raw object did not change after integration")

    actual_externals = external_reference_signatures(output_environment)
    if actual_externals != expected_externals:
        raise IntegrationError("Serialized external references changed")
    actual_resources = resource_hashes(output_environment)
    if actual_resources != expected_resources:
        raise IntegrationError("Bundle resource stream bytes changed")

    output_tile_summary = tile_summary(output_tree)
    if output_tile_summary["count"] != len(source_tree["m_NavMeshTiles"]):
        raise IntegrationError("Output NavMeshData tile count differs from source")
    return {
        "objects_verified": len(actual_objects),
        "unrelated_objects_unchanged": len(actual_objects) - 1,
        "serialized_external_references_verified": sum(len(items) for items in actual_externals.values()),
        "resource_streams_verified": len(actual_resources),
        "navmesh": output_tile_summary,
        "output_object_sha256": actual_objects[expected_nav_key],
    }


def integrate(asset: Path, bundle: Path, output: Path, report_path: Path) -> dict[str, Any]:
    if _resolved(asset) == _resolved(output):
        raise IntegrationError("Output must be a separate file from --asset; refusing in-place rewrite")
    if _resolved(bundle) == _resolved(output):
        raise IntegrationError("Output must be a separate file from --bundle; refusing in-place rewrite")
    if _resolved(report_path) in {_resolved(asset), _resolved(bundle), _resolved(output)}:
        raise IntegrationError("--report must be a separate file from every input and output")
    for path, label in ((asset, "asset"), (bundle, "bundle")):
        if not path.is_file():
            raise IntegrationError(f"{label} does not exist: {path}")

    source_sha256 = sha256_file(asset)
    bundle_sha256 = sha256_file(bundle)
    source_environment = UnityPy.load(str(asset))
    if len(source_environment.files) != 1:
        raise IntegrationError(f"Source asset must contain one SerializedFile; found {len(source_environment.files)}")
    source_nav = require_single_nav(source_environment, "Source asset")
    source_tree = source_nav.read_typetree()
    source_pointers = nonzero_pointers(source_tree, ("NavMeshData", int(source_nav.path_id)))
    if source_pointers:
        raise IntegrationError(f"Source asset contains nonzero object pointers: {source_pointers[:8]}")
    source_summary = tile_summary(source_tree)

    target_environment = UnityPy.load(str(bundle))
    if len(target_environment.files) != 1:
        raise IntegrationError(f"Target bundle must contain one AssetBundle file; found {len(target_environment.files)}")
    target_bundle = next(iter(target_environment.files.values()))
    if not hasattr(target_bundle, "files") or not hasattr(target_bundle, "save"):
        raise IntegrationError("Target input is not a Unity AssetBundle")
    target_nav = require_single_nav(target_environment, "Target bundle")
    if target_nav.assets_file.name != EXPECTED_NAV_ASSETS_FILE:
        raise IntegrationError(
            f"Target NavMeshData serialized file must be {EXPECTED_NAV_ASSETS_FILE}; "
            f"found {target_nav.assets_file.name}"
        )
    if int(target_nav.path_id) != EXPECTED_NAV_PATH_ID:
        raise IntegrationError(
            f"Target NavMeshData path ID must be {EXPECTED_NAV_PATH_ID}; found {target_nav.path_id}"
        )
    target_tree = target_nav.read_typetree()
    target_field_set = set(target_tree)
    missing_source_fields = sorted(target_field_set - set(source_tree))
    if missing_source_fields:
        raise IntegrationError(f"Source asset lacks target runtime fields: {missing_source_fields}")
    if "m_Name" not in target_field_set:
        raise IntegrationError("Target NavMeshData runtime field set lacks m_Name")

    if source_tree["m_AgentTypeID"] != target_tree["m_AgentTypeID"]:
        raise IntegrationError("Source and target NavMeshData agent type IDs do not match")
    if not values_equal(source_tree["m_NavMeshBuildSettings"], target_tree["m_NavMeshBuildSettings"]):
        raise IntegrationError("Source and target NavMeshData build settings do not match")
    if not values_equal(normalize_zero(source_tree["m_Rotation"]), normalize_zero(target_tree["m_Rotation"])):
        raise IntegrationError("Source and target NavMeshData origin rotations do not match")
    if not values_equal(normalize_zero(source_tree["m_Position"]), normalize_zero(target_tree["m_Position"])):
        raise IntegrationError("Source and target NavMeshData origins do not match")
    target_summary = tile_summary(target_tree)

    expected_objects = object_hashes(target_environment)
    expected_nav_key = object_key(target_nav)
    expected_externals = external_reference_signatures(target_environment)
    expected_resources = resource_hashes(target_environment)
    target_name = target_tree["m_Name"]

    projected_tree = {key: source_tree[key] for key in target_field_set}
    projected_tree["m_Name"] = target_name
    target_nav.save_typetree(projected_tree)

    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(target_bundle.save(packer="lz4"))
    if sha256_file(bundle) != bundle_sha256:
        raise IntegrationError("Target bundle changed while writing the separate output")

    verification = verify_output(
        output,
        source_tree,
        target_field_set,
        target_name,
        expected_objects,
        expected_externals,
        expected_resources,
        expected_nav_key,
    )
    output_sha256 = sha256_file(output)
    report = {
        "status": "passed",
        "source_asset": {
            "path": str(asset),
            "sha256": source_sha256,
            "unity_version": source_nav.assets_file.unity_version,
            "serialized_file": str(source_nav.assets_file.name),
            "path_id": int(source_nav.path_id),
            "name": source_tree["m_Name"],
            "tiles": source_summary,
        },
        "input_bundle": {
            "path": str(bundle),
            "sha256": bundle_sha256,
            "unity_version": target_nav.assets_file.unity_version,
            "serialized_file": str(target_nav.assets_file.name),
            "path_id": int(target_nav.path_id),
            "name_before": target_name,
            "tiles_before": target_summary,
        },
        "output_bundle": {
            "path": str(output),
            "sha256": output_sha256,
            "name_after": target_name,
            "tiles_after": verification["navmesh"],
        },
        "fields_projected": sorted(target_field_set - {"m_Name"}),
        "objects_verified": verification["objects_verified"],
        "unrelated_objects_unchanged": verification["unrelated_objects_unchanged"],
        "serialized_external_references_verified": verification["serialized_external_references_verified"],
        "resource_streams_verified": verification["resource_streams_verified"],
        "source_nonzero_object_pointers": source_pointers,
        "output_nonzero_object_pointers": [],
        "gameplay_proof": False,
        "scope": (
            "Serialized NavMeshData field projection and Unity bundle integrity only; "
            "no gameplay or in-raid pathfinding proof is provided."
        ),
    }
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return report


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--asset", required=True, type=Path, help="Imported source NavMeshData asset")
    parser.add_argument("--bundle", required=True, type=Path, help="Existing runtime AssetBundle")
    parser.add_argument("--output", required=True, type=Path, help="Separate output AssetBundle path")
    parser.add_argument("--report", required=True, type=Path, help="JSON verification report path")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    try:
        report = integrate(args.asset.resolve(), args.bundle.resolve(), args.output.resolve(), args.report.resolve())
    except (IntegrationError, FileNotFoundError, OSError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1
    print(
        json.dumps(
            {
                "status": report["status"],
                "output": report["output_bundle"]["path"],
                "report": str(args.report.resolve()),
                "tiles": report["output_bundle"]["tiles_after"]["count"],
                "objects_verified": report["objects_verified"],
            },
            indent=2,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
