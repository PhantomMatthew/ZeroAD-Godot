"""Audit GLBs whose node scales contain the DAE <unit> factor, and fix the broken ones.

Background: see fix_glb_unit_node_scale.py. Blender's DAE importer honours
<unit meter="u"/> and multiplies u into node scales; the 0 A.D. engine treats
raw DAE coordinates as meters. Whether that hurts depends on the file:

- BROKEN: vertices stayed raw, so the composed (node-transform-applied) render
  size is u x the DAE's raw extents — the model renders at ~2.5% size.
- FINE: vertices were pre-scaled by 1/u during conversion, so composed size
  already matches the DAE raw extents — the unit scale is cancelled out.

This script computes the composed AABB of every GLB (node graph x mesh vertex
extents), compares it against the source DAE's raw max position coordinate,
and only then strips the unit factor from contaminated node scales (the rule
from fix_glb_unit_node_scale.py: every scale component within 1.5*u and at
least one within 2% of u). GLBs with skins are left untouched — armature
scales participate in skinning and are out of scope.

Usage:
  python3 fix_glb_unit_node_scale_audit.py --scan          # report only
  python3 fix_glb_unit_node_scale_audit.py --fix           # repair broken ones
"""
from __future__ import annotations

import argparse
import contextlib
import json
import math
import re
import struct
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


UNIT_RE = re.compile(r'<unit[^>]*meter="([\d.eE+-]+)"')
FLOAT_ARRAY_RE = re.compile(r'<float_array[^>]*id="([^"]*)"[^>]*>([^<]+)</float_array>', re.DOTALL)
COLLADA_NS = "{http://www.collada.org/2005/11/COLLADASchema}"

# A node scale is "unit-contaminated" when every component could be authored
# (~0.5..1.5 after dividing by u) and at least one component equals u closely.
SANE_LO, SANE_HI = 0.5, 1.5
UNIT_TOL = 0.02


def read_glb(path: Path) -> tuple[dict, bytes]:
    data = path.read_bytes()
    magic, _version, _length = struct.unpack_from("<III", data, 0)
    if magic != 0x46546C67:
        raise ValueError("not a GLB")
    json_len, json_type = struct.unpack_from("<II", data, 12)
    if json_type != 0x4E4F534A:
        raise ValueError("first chunk is not JSON")
    return json.loads(data[20 : 20 + json_len]), data[20 + json_len :]


def write_glb(path: Path, gltf: dict, rest: bytes) -> None:
    payload = json.dumps(gltf, separators=(",", ":")).encode()
    payload += b" " * ((4 - len(payload) % 4) % 4)
    total = 12 + 8 + len(payload) + len(rest)
    header = struct.pack("<III", 0x46546C67, 2, total)
    chunk = struct.pack("<II", len(payload), 0x4E4F534A)
    path.write_bytes(header + chunk + payload + rest)


def dae_info(dae_path: Path) -> tuple[float, float] | None:
    """(unit_meter, max abs raw position coordinate) from the source DAE."""
    try:
        text = dae_path.read_text(errors="ignore")
    except OSError:
        return None
    m = UNIT_RE.search(text)
    unit = float(m.group(1)) if m else 1.0
    max_coord = 0.0
    for arr in FLOAT_ARRAY_RE.finditer(text):
        if "position" not in arr.group(1).lower():
            continue
        for tok in arr.group(2).split():
            with contextlib.suppress(ValueError):
                max_coord = max(max_coord, abs(float(tok)))
    if max_coord == 0.0:
        return None
    return unit, max_coord


def dae_node_translations(dae_path: Path) -> dict[str, list[float]]:
    """{node_name: Y-up local translation} for DAE nodes that carry a <translate>.

    Z-up (X,Y,Z) → Y-up (X,Z,Y), matching the PMD converter and Blender export_yup.
    Used as ground truth to detect translations Blender multiplied by the unit
    factor (e.g. rome_tower 'rof': 20.87 m became 0.53).
    """
    try:
        root = ET.parse(dae_path).getroot()
    except (OSError, ET.ParseError):
        return {}
    out: dict[str, list[float]] = {}
    for scene in root.iter(COLLADA_NS + "visual_scene"):
        for node in scene.iter(COLLADA_NS + "node"):
            name = node.get("name") or node.get("id")
            if not name:
                continue
            tr = node.find(COLLADA_NS + "translate")
            if tr is None or not tr.text:
                continue
            vals = tr.text.split()
            if len(vals) != 3:
                continue
            try:
                x, y, z = (float(v) for v in vals)
            except ValueError:
                continue
            out[name] = [x, z, y]
    return out


def glb_positions_max(gltf: dict, mesh_index: int) -> float:
    """Max abs raw POSITION coordinate of one mesh's primitives."""
    max_coord = 0.0
    mesh = gltf["meshes"][mesh_index]
    for prim in mesh.get("primitives", []):
        pos = prim.get("attributes", {}).get("POSITION")
        if pos is None:
            continue
        accessor = gltf["accessors"][pos]
        for key in ("min", "max"):
            for v in accessor.get(key, []):
                max_coord = max(max_coord, abs(v))
    return max_coord


def node_local_scale(node: dict) -> tuple[float, float, float]:
    s = node.get("scale")
    if s:
        return (abs(s[0]), abs(s[1]), abs(s[2]))
    mat = node.get("matrix")
    if mat:  # column-major 4x4; scale = column vector lengths
        return (
            math.sqrt(mat[0] ** 2 + mat[1] ** 2 + mat[2] ** 2),
            math.sqrt(mat[4] ** 2 + mat[5] ** 2 + mat[6] ** 2),
            math.sqrt(mat[8] ** 2 + mat[9] ** 2 + mat[10] ** 2),
        )
    return (1.0, 1.0, 1.0)


def glb_composed_max(gltf: dict) -> float:
    """Max abs coordinate after applying the node-graph scales (translations
    don't affect extents ordering here — positions dominate).
    """
    nodes = gltf.get("nodes", [])
    parents: dict[int, int] = {}
    for i, n in enumerate(nodes):
        for c in n.get("children", []):
            parents[c] = i

    def acc_scale(i: int) -> float:
        sx, sy, sz = 1.0, 1.0, 1.0
        while True:
            lx, ly, lz = node_local_scale(nodes[i])
            sx, sy, sz = sx * lx, sy * ly, sz * lz
            if i not in parents:
                return max(sx, sy, sz)
            i = parents[i]

    composed = 0.0
    for i, n in enumerate(nodes):
        if "mesh" not in n:
            continue
        composed = max(composed, glb_positions_max(gltf, n["mesh"]) * acc_scale(i))
    return composed


def contaminated(node: dict, unit: float) -> bool:
    s = node.get("scale")
    if not s:
        return False
    if not all(abs(v) <= SANE_HI * unit for v in s):
        return False
    return any(abs(abs(v) - unit) < UNIT_TOL * unit for v in s)


def process(glb_path: Path, dae_root: Path, meshes_root: Path, fix: bool) -> str:
    rel = glb_path.relative_to(meshes_root)
    gltf, rest = read_glb(glb_path)

    dae_path = (dae_root / rel).with_suffix(".dae")
    info = dae_info(dae_path)
    if info is None:
        return "skip:no-dae"
    unit, dae_max = info
    if abs(unit - 1.0) < 1e-6:
        return "skip:unit=1"
    if gltf.get("skins"):
        return "skip:skinned"

    bad_nodes = [n.get("name", "?") for n in gltf.get("nodes", []) if contaminated(n, unit)]
    # Translations contaminated by the unit factor (GLB_T ≈ DAE_T x u). Only
    # restored against DAE ground truth — raw translations (field patches) stay.
    dae_trans = dae_node_translations(dae_path)
    bad_trans: list[str] = []
    for node in gltf.get("nodes", []):
        t = node.get("translation")
        name = node.get("name")
        if not t or name is None:
            continue
        # Blender 重命名差异:GLB 的 '.' 对应 DAE 的 '_'(Cube.001 ↔ Cube_001)
        want = dae_trans.get(name) or dae_trans.get(name.replace(".", "_"))
        if want is None:
            continue
        matches_scaled = all(
            abs(t[i] - want[i] * unit) <= 0.02 * max(1.0, abs(want[i] * unit)) for i in range(3)
        )
        if matches_scaled and any(abs(want[i]) > 1.0 for i in range(3)):
            bad_trans.append(name)

    if not bad_nodes and not bad_trans:
        return "skip:clean"

    composed = glb_composed_max(gltf)
    if composed == 0.0:
        return "skip:no-mesh-bounds"
    ratio = composed / dae_max
    if abs(ratio - 1.0) < 0.15 and not bad_trans:
        return f"ok:cancelled (composed/dae={ratio:.3f})"
    if abs(ratio - unit) > 0.15 * unit and abs(ratio - 1.0) >= 0.15:
        return f"REVIEW:ratio={ratio:.4f} neither 1 nor unit (nodes={bad_nodes[:3]})"

    tag = f"ratio={ratio:.4f} nodes={bad_nodes[:3]} trans={bad_trans[:3]}"
    if not fix:
        return f"BROKEN:{tag}"
    for node in gltf.get("nodes", []):
        if node.get("scale") and contaminated(node, unit):
            node["scale"] = [v / unit for v in node["scale"]]
        name = node.get("name")
        if name in bad_trans:
            node["translation"] = dae_trans.get(name) or dae_trans[name.replace(".", "_")]
    write_glb(glb_path, gltf, rest)
    return f"FIXED:{tag}"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--meshes-root", default="godot/assets/meshes")
    parser.add_argument("--dae-root", default="binaries/data/mods/public/art/meshes")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--scan", action="store_true", help="只报告(默认)")
    mode.add_argument("--fix", action="store_true", help="修复确认损坏的 GLB")
    args = parser.parse_args()

    meshes_root = Path(args.meshes_root)
    dae_root = Path(args.dae_root)
    counts: dict[str, int] = {}
    for glb_path in sorted(meshes_root.rglob("*.glb")):
        try:
            outcome = process(glb_path, dae_root, meshes_root, args.fix)
        except (ValueError, OSError, KeyError, IndexError, struct.error) as exc:
            outcome = f"error:{exc}"
        key = outcome.split(":")[0]
        counts[key] = counts.get(key, 0) + 1
        if key not in ("skip:clean", "ok"):
            print(f"  {glb_path.relative_to(meshes_root)}: {outcome}")
    print("summary:", dict(sorted(counts.items())))
    return 0


if __name__ == "__main__":
    sys.exit(main())
