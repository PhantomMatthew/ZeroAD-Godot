"""Make GLB composed sizes match the original engine's interpretation of DAE files.

The 0 A.D. C++ collada converter reads only the up-axis and IGNORES the DAE
`<unit meter="X"/>` declaration (see source/collada/CommonConvert.cpp - the
StandardizeUpAxisAndLength call is only a TODO). Raw DAE coordinates ARE game
meters. Converters that honored the unit metadata (root unit-scale matrix or
vertex rescale) therefore shrink models vs the original game, e.g. a town
center composing to 0.73 m instead of 28.8 m.

Fix rule: for every GLB, set the root scale to dae_max/glb_max computed from
raw vertex extents, so composed size == raw DAE coordinates. Corrections are
only applied when they match unit^k (k in 1..2) patterns to guard against
fingerprint noise, and skinned GLBs are left untouched.

Usage: python3 fix_glb_unit_scale.py [--dry-run] [--meshes-root PATH] [--dae-root PATH]
"""

from __future__ import annotations

import argparse
import json
import math
import re
import struct
import sys
from pathlib import Path

COMPOSED_DEVIATION_TRIGGER = 0.5
PATTERN_TOL = 0.15


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
    """Return (unit_meter, max_abs_position_coordinate) from the source DAE."""
    try:
        text = dae_path.read_text(errors="ignore")
    except OSError:
        return None
    unit_match = re.search(r'<unit[^>]*meter="([\d.eE+-]+)"', text)
    unit = float(unit_match.group(1)) if unit_match else 1.0
    max_coord = 0.0
    for arr in re.finditer(r'<float_array[^>]*id="([^"]*)"[^>]*>([^<]+)</float_array>', text):
        if "position" not in arr.group(1).lower():
            continue
        for tok in arr.group(2).split():
            try:
                max_coord = max(max_coord, abs(float(tok)))
            except ValueError:
                pass
    if max_coord == 0.0:
        return None
    return unit, max_coord


def glb_max_coord(gltf: dict) -> float:
    max_coord = 0.0
    for mesh in gltf.get("meshes", []):
        for prim in mesh.get("primitives", []):
            pos = prim.get("attributes", {}).get("POSITION")
            if pos is None:
                continue
            acc = gltf["accessors"][pos]
            for v in (acc.get("min") or []) + (acc.get("max") or []):
                max_coord = max(max_coord, abs(v))
    return max_coord


def uniform_scale(node: dict) -> float | None:
    """Uniform scale of a root node, or None if non-uniform/translated."""
    if "matrix" in node:
        m = node["matrix"]
        sx = math.sqrt(m[0] ** 2 + m[1] ** 2 + m[2] ** 2)
        sy = math.sqrt(m[4] ** 2 + m[5] ** 2 + m[6] ** 2)
        sz = math.sqrt(m[8] ** 2 + m[9] ** 2 + m[10] ** 2)
        if sx == 0 or abs(sx - sy) > 1e-3 * sx or abs(sx - sz) > 1e-3 * sx:
            return None
        if any(abs(m[i]) > 1e-4 for i in (12, 13, 14)):
            return None
        return sx
    scale = node.get("scale")
    if scale is not None and (
        abs(scale[0] - scale[1]) > 1e-3 or abs(scale[0] - scale[2]) > 1e-3
    ):
        return None
    if any(abs(t) > 1e-4 for t in node.get("translation", [0, 0, 0])):
        return None
    return scale[0] if scale else 1.0


def apply_scale(node: dict, factor: float) -> None:
    if "matrix" in node:
        m = node["matrix"]
        for col in (0, 4, 8):
            for i in range(3):
                m[col + i] *= factor
    else:
        cur = node.get("scale", [1.0, 1.0, 1.0])
        new = [cur[0] * factor, cur[1] * factor, cur[2] * factor]
        if all(abs(v - 1.0) < 1e-6 for v in new):
            node.pop("scale", None)
        else:
            node["scale"] = new


def matches_pattern(value: float, unit: float) -> bool:
    if abs(unit - 1.0) < 1e-6:
        return False
    for k in (1, 2):
        for target in (unit**k, unit**-k):
            if abs(value - target) < PATTERN_TOL * target:
                return True
    return False


def process(glb_path: Path, dae_root: Path, meshes_root: Path, dry_run: bool) -> str:
    rel = glb_path.relative_to(meshes_root)
    gltf, rest = read_glb(glb_path)
    if gltf.get("skins"):
        return "skip:skinned"

    info = dae_info((dae_root / rel).with_suffix(".dae"))
    if info is None:
        return "skip:no-dae-info"
    unit, dae_max = info

    glb_max = glb_max_coord(gltf)
    if glb_max == 0.0:
        return "skip:no-geometry"

    scene = gltf.get("scenes", [{}])[gltf.get("scene", 0)]
    roots = [gltf["nodes"][i] for i in scene.get("nodes", [])]
    scales = [uniform_scale(n) for n in roots]
    if not roots or any(s is None for s in scales):
        return "skip:complex-roots"
    if max(scales) - min(scales) > 1e-3 * max(scales):
        return "skip:mixed-root-scales"
    s_cur = scales[0]

    composed = (glb_max / dae_max) * s_cur
    if abs(composed - 1.0) <= COMPOSED_DEVIATION_TRIGGER:
        return "ok:already-raw-scale"
    correction = 1.0 / composed
    if not matches_pattern(correction, unit):
        return f"skip:ambiguous-correction-{correction:.4f}-unit-{unit:.4f}"

    if dry_run:
        return f"would-fix:x{correction:.4f}"
    for node in roots:
        apply_scale(node, correction)
    write_glb(glb_path, gltf, rest)
    return f"fixed:x{correction:.4f}"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--meshes-root", default="godot/assets/meshes")
    parser.add_argument("--dae-root", default="binaries/data/mods/public/art/meshes")
    args = parser.parse_args()

    meshes_root = Path(args.meshes_root)
    dae_root = Path(args.dae_root)
    results: dict[str, list[str]] = {}
    for glb_path in sorted(meshes_root.rglob("*.glb")):
        try:
            outcome = process(glb_path, dae_root, meshes_root, args.dry_run)
        except (ValueError, OSError, KeyError, IndexError, struct.error) as exc:
            outcome = f"error:{exc}"
        results.setdefault(outcome.split(":x")[0] if ":x" in outcome else outcome, []).append(
            f"{glb_path} [{outcome}]"
        )

    for outcome in sorted(results):
        print(f"{outcome}: {len(results[outcome])}")
    manifest = Path("/tmp/glb_unit_scale_fix_manifest.json")
    manifest.write_text(json.dumps(results, indent=1))
    print(f"manifest: {manifest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
