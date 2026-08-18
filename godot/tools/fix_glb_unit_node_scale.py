"""Strip the DAE <unit> factor baked into GLB node scales.

Blender's DAE importer honours the <unit meter="u"/> declaration and multiplies
u into node scales (e.g. field_propped_*8x8: Plane scale 0.8768 becomes
0.8768x0.0254=0.0223). The 0 A.D. engine IGNORES <unit> — raw DAE coordinates
are game meters (source/collada/CommonConvert.cpp: StandardizeUpAxisAndLength
is only a TODO). The contaminated scale then:

- shrinks the node's own mesh (field plot quad renders at 0.25 m), and
- crushes every prop attached to a child prop-point: prop-patch_* translations
  stay raw (fix_glb_field_patches.py restored them) but inherit the unit-scaled
  parent, so 64 wheat props crowd into the centre ~1 m at 2.5% size instead of
  tiling the field at full size.

Fix rule: a node's scale is divided by u only when EVERY component is within
1.5*u (i.e. the whole scale is the unit factor times an authored ~1 scale) AND
at least one component matches u within 2%. Compensation scales like the patch
nodes' 1.1405 (= 1/0.8768) are far outside that band and stay untouched.

Usage: python3 fix_glb_unit_node_scale.py [--dry-run] [--meshes-root PATH] [--dae-root PATH]
"""
from __future__ import annotations

import argparse
import json
import re
import struct
import sys
from pathlib import Path


UNIT_RE = re.compile(r'<unit[^>]*meter="([\d.eE+-]+)"')


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


def dae_unit(dae_path: Path) -> float:
    try:
        text = dae_path.read_text(errors="ignore")
    except OSError:
        return 1.0
    m = UNIT_RE.search(text)
    return float(m.group(1)) if m else 1.0


def process(glb_path: Path, dae_root: Path, meshes_root: Path, dry_run: bool) -> str:
    rel = glb_path.relative_to(meshes_root)
    dae_path = (dae_root / rel).with_suffix(".dae")
    unit = dae_unit(dae_path)
    if abs(unit - 1.0) < 1e-6:
        return "skip:unit=1"

    gltf, rest = read_glb(glb_path)
    fixed: list[str] = []
    for node in gltf.get("nodes", []):
        scale = node.get("scale")
        if not scale:
            continue
        if not all(abs(v) <= 1.5 * unit for v in scale):
            continue  # 含明显大于单位因子的分量 → 是 authored/补偿 scale,不动
        if not any(abs(abs(v) - unit) < 0.02 * unit for v in scale):
            continue  # 没有任何分量等于单位因子 → 未被污染
        node["scale"] = [v / unit for v in scale]
        fixed.append(node.get("name", "?"))

    if not fixed:
        return "ok:no-contaminated-node"
    if not dry_run:
        write_glb(glb_path, gltf, rest)
    return f"{'would-fix' if dry_run else 'fixed'}:{','.join(fixed)}"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--meshes-root", default="godot/assets/meshes")
    parser.add_argument("--dae-root", default="binaries/data/mods/public/art/meshes")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument(
        "--filter", default="field_propped",
        help="只处理文件名含此串的 GLB(默认 field_propped)",
    )
    args = parser.parse_args()

    meshes_root = Path(args.meshes_root)
    dae_root = Path(args.dae_root)
    count = 0
    for glb_path in sorted(meshes_root.rglob("*.glb")):
        if args.filter and args.filter not in glb_path.name:
            continue
        try:
            outcome = process(glb_path, dae_root, meshes_root, args.dry_run)
        except (ValueError, OSError, KeyError, struct.error) as exc:
            outcome = f"error:{exc}"
        print(f"  {glb_path.relative_to(meshes_root)}: {outcome}")
        if outcome.startswith(("fixed:", "would-fix:")):
            count += 1
    print(f"repaired {count} GLB(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
