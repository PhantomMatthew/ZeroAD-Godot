#!/usr/bin/env python3
"""Re-convert empty GLBs produced by the Blender DAE pipeline.

Blender 4.0 removed the built-in Collada importer, so on newer Blender
installs `convert_dae_to_gltf.py` fails to import and leaves 132-byte
empty GLBs behind. This script finds every empty GLB under
`godot/assets/meshes/` and re-converts it from the upstream DAE with
trimesh + pycollada (no Blender needed).

Scope: static meshes only (trees, props, rocks — the failure set is all
static). Vertex normals and UVs are preserved; the Y-up convention matches
the Blender pipeline output for static meshes (verified by bbox comparison
against known-good conversions like gaia/tree_oak_a.glb).

Usage:
    python3 -m venv .venv-dae && . .venv-dae/bin/activate
    pip install trimesh pycollada numpy
    python tools/reconvert_empty_glb.py            # dry run
    python tools/reconvert_empty_glb.py --write    # actually write GLBs

Paths follow the same convention as the other tools: run from `godot/`,
upstream tree located via the `binaries/` junction.
"""

import argparse
import json
import struct
from pathlib import Path

import trimesh

GODOT_MESHES = Path(__file__).resolve().parent.parent / "assets" / "meshes"
UPSTREAM_MESHES = (
    Path(__file__).resolve().parent.parent.parent
    / "binaries" / "data" / "mods" / "public" / "art" / "meshes"
)


def is_empty_glb(path: Path) -> bool:
    """A GLB with zero scene nodes (failed conversion artifact)."""
    try:
        data = path.read_bytes()
        (json_len,) = struct.unpack("<I", data[12:16])
        doc = json.loads(data[20 : 20 + json_len])
        return len(doc.get("nodes", [])) == 0
    except Exception:
        return True


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--write", action="store_true", help="write converted GLBs (default: dry run)"
    )
    args = parser.parse_args()

    converted, failed, missing = [], [], []
    for glb in sorted(GODOT_MESHES.rglob("*.glb")):
        if not is_empty_glb(glb):
            continue
        rel = glb.relative_to(GODOT_MESHES)
        if rel.parts[0] == "temp":
            continue
        dae = UPSTREAM_MESHES / rel.with_suffix(".dae")
        if not dae.is_file():
            missing.append(rel)
            continue
        if not args.write:
            converted.append(rel)
            continue
        try:
            loaded = trimesh.load(dae, process=False)
            mesh = loaded.to_mesh() if isinstance(loaded, trimesh.Scene) else loaded
            # Force vertex-normal computation so the GLB carries NORMAL.
            _ = mesh.vertex_normals
            mesh.export(glb)
            converted.append(rel)
        except Exception as exc:  # noqa: BLE001 — report and continue the batch
            failed.append((rel, str(exc)[:80]))

    print(f"converted {len(converted)}, failed {len(failed)}, missing-src {len(missing)}")
    for rel, err in failed:
        print("FAIL", rel, err)
    for rel in missing:
        print("MISS", rel)


if __name__ == "__main__":
    main()
