#!/usr/bin/env python3
"""
Convert a focused subset of 0 A.D. animation DAEs to GLB.
Preserves relative path structure under assets/animations/.
Requires Blender 4.2 LTS.
"""

import bpy
import sys
import os
from pathlib import Path

ANIM_SRC = Path("../binaries/data/mods/public/art/animation")
OUT = Path("assets/animations")

# Manifest file: one DAE path (relative to art/animation/) per line.
# Default covers every animation referenced by the Athenian citizen (both
# phenotypes) and infantry spearman actors — the units our sim actually spawns.
# Regenerate with the variant-chain walker when adding new unit actors.
MANIFEST_FILE = Path(os.environ.get("ANIM_MANIFEST", "/tmp/anim_manifest.txt"))

FALLBACK_MANIFEST = [
    "biped/infantry/hoplite/idle_relax_01.dae",
    "biped/infantry/hoplite/idle_relax_02.dae",
    "biped/infantry/hoplite/walk_relax.dae",
    "biped/infantry/hoplite/run_relax.dae",
    "biped/infantry/hoplite/attack_melee_04.dae",
    "biped/infantry/hoplite/attack_melee_05.dae",
    "biped/infantry/death_a.dae",
    "biped/infantry/death_b.dae",
    "quadraped/horse_idle_01.dae",
    "quadraped/horse_idle_02.dae",
    "quadraped/horse_trot.dae",
    "quadraped/horse_gallop.dae",
    "quadraped/horse_attack_01.dae",
    "quadraped/horse_death_01.dae",
    "quadraped/horse_death_02.dae",
]

MANIFEST = (
    [line.strip() for line in MANIFEST_FILE.read_text().splitlines() if line.strip()]
    if MANIFEST_FILE.exists()
    else FALLBACK_MANIFEST
)


def convert(rel: str) -> bool:
    src = ANIM_SRC / rel
    dst = OUT / rel.replace(".dae", ".glb")
    if not src.exists():
        print(f"  SKIP (missing): {rel}")
        return False

    dst.parent.mkdir(parents=True, exist_ok=True)

    try:
        bpy.ops.wm.read_factory_settings(use_empty=True)
    except Exception:
        pass

    try:
        bpy.ops.wm.collada_import(filepath=str(src))
    except Exception as e:
        print(f"  FAIL (import): {rel}: {e}")
        return False

    try:
        bpy.ops.export_scene.gltf(
            filepath=str(dst),
            export_format="GLB",
            export_animations=True,
            export_animation_mode="ACTIONS",
            export_yup=True,
            export_skins=True,
        )
        print(f"  OK: {rel} -> {dst.relative_to(OUT)}")
        return True
    except Exception as e:
        print(f"  FAIL (export): {rel}: {e}")
        return False


def main():
    argv = sys.argv
    if "--" in argv:
        argv = argv[argv.index("--") + 1:]
    else:
        argv = []

    ok = 0
    fail = 0
    for rel in MANIFEST:
        if convert(rel):
            ok += 1
        else:
            fail += 1

    print(f"\nDone: {ok} converted, {fail} failed, {len(MANIFEST)} total")
    print(f"Output: {OUT}/")


if __name__ == "__main__":
    main()
