"""Restore field crop-patch grid translations lost in DAE→GLB conversion.

The 0 A.D. field meshes (field_propped_*8x8.dae) carry 64 `prop-patch_NNN`
Empty nodes, each with a <translate> spreading crop props across an 8×8 grid.
Blender's glTF export (export_apply=True) collapses these transform-only
Empties, zeroing every translation — so all 64 crops stack at field center
instead of tiling the field.

This post-processor reads the patch translations straight from the source DAE
(axis-swapped Z-up→Y-up: DAE (X,Y,Z) → GLB (X,Z,Y), matching the engine's
PMD converter and Blender's export_yup) and writes them back onto the matching
`prop-patch_NNN` nodes in the GLB. Idempotent: skips nodes that already carry
a translation.

Usage: python3 fix_glb_field_patches.py [--meshes-root PATH] [--dae-root PATH]
"""
from __future__ import annotations

import argparse
import json
import re
import struct
import sys
from pathlib import Path

PATCH_NODE_RE = re.compile(
    r'<node id="(prop-patch[^"]*)"[^>]*>(.*?)</node>', re.S
)
TRANSLATE_RE = re.compile(r'<translate[^>]*>([^<]+)</translate>')


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


def dae_patch_translations(dae_path: Path) -> dict[str, list[float]]:
    """Return {node_name: [x, y, z]} for every prop-patch_* node in the DAE (Z-up)."""
    text = dae_path.read_text(errors="ignore")
    out: dict[str, list[float]] = {}
    for m in PATCH_NODE_RE.finditer(text):
        name = m.group(1)
        tr = TRANSLATE_RE.search(m.group(2))
        if tr:
            vals = tr.group(1).split()
            if len(vals) == 3:
                out[name] = [float(v) for v in vals]
    return out


def zup_to_yup(x: float, y: float, z: float) -> list[float]:
    # DAE Z-up (X east, Y south, Z up) → glTF Y-up (X east, Y up, Z south).
    # Matches the 0 A.D. PMD converter's (x,y,z)→(x,z,y) and Blender export_yup.
    return [x, z, y]


def process(glb_path: Path, dae_root: Path, meshes_root: Path) -> str:
    rel = glb_path.relative_to(meshes_root)
    gltf, rest = read_glb(glb_path)
    dae_path = (dae_root / rel).with_suffix(".dae")
    if not dae_path.exists():
        return "skip:no-dae"
    patches = dae_patch_translations(dae_path)
    if not patches:
        return "skip:no-patch-nodes-in-dae"

    by_name = {n.get("name", ""): n for n in gltf.get("nodes", [])}
    fixed = 0
    skipped_has_translation = 0
    for name, xyz in patches.items():
        node = by_name.get(name)
        if node is None:
            continue
        if node.get("translation") is not None:
            skipped_has_translation += 1
            continue
        node["translation"] = zup_to_yup(xyz[0], xyz[1], xyz[2])
        fixed += 1

    if fixed == 0:
        return f"ok:already-fixed({skipped_has_translation})"
    write_glb(glb_path, gltf, rest)
    return f"fixed:{fixed}/{len(patches)}"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--meshes-root", default="godot/assets/meshes")
    parser.add_argument("--dae-root", default="binaries/data/mods/public/art/meshes")
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
            outcome = process(glb_path, dae_root, meshes_root)
        except (ValueError, OSError, KeyError, struct.error) as exc:
            outcome = f"error:{exc}"
        print(f"  {glb_path.relative_to(meshes_root)}: {outcome}")
        if outcome.startswith("fixed:"):
            count += 1
    print(f"repaired {count} field GLB(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
