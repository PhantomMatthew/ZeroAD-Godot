#!/usr/bin/env python3
"""
Unified 0 A.D. asset pipeline — converts ALL DAE to GLB via Blender 4.2 LTS.
Handles: static meshes (buildings/trees/props/gaia), skeletal meshes, props.

Usage:
    "/Applications/Blender 4.2 LTS.app/Contents/MacOS/Blender" --background \
        --python convert_all_assets.py -- \
        --input  <0ad>/binaries/data/mods/public/art/meshes \
        --output <godot>/assets/meshes \
        [--max N] [--filter pattern]
"""

import bpy
import sys
import os
import argparse
import traceback
from pathlib import Path


def clear_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def convert_one(dae_path, output_path):
    clear_scene()
    try:
        bpy.ops.wm.collada_import(filepath=dae_path)
    except Exception as e:
        print(f"  SKIP import: {os.path.basename(dae_path)} — {e}")
        return False

    os.makedirs(os.path.dirname(output_path), exist_ok=True)

    has_armature = any(o.type == "ARMATURE" for o in bpy.data.objects)

    try:
        export_kwargs = dict(
            filepath=output_path,
            export_format="GLB",
            export_yup=True,
            export_apply=False,
        )
        if has_armature:
            export_kwargs.update(
                export_skins=True,
                export_animations=True,
                export_animation_mode="ACTIONS",
            )

        bpy.ops.export_scene.gltf(**export_kwargs)
        return True
    except Exception as e:
        print(f"  FAIL export: {os.path.basename(dae_path)} — {e}")
        return False


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description="0 A.D. batch DAE→GLB converter")
    parser.add_argument("--input", required=True, help="Input art/meshes directory")
    parser.add_argument("--output", required=True, help="Output directory")
    parser.add_argument("--max", type=int, default=0, help="Max files (0=all)")
    parser.add_argument("--filter", default="*.dae", help="File pattern")
    parser.add_argument("--start", type=int, default=0, help="Skip first N files")
    args = parser.parse_args(argv)

    import fnmatch
    dae_files = []
    for root, dirs, files in os.walk(args.input):
        for f in sorted(files):
            if f.endswith(".dae") and fnmatch.fnmatch(f, args.filter):
                dae_files.append(os.path.join(root, f))

    dae_files.sort()
    total = len(dae_files)
    print(f"Found {total} DAE files")

    if args.start > 0:
        dae_files = dae_files[args.start:]
        print(f"Skipping first {args.start}, starting at {args.start}")

    if args.max > 0:
        dae_files = dae_files[:args.max]

    ok = 0
    fail = 0
    for i, dae in enumerate(dae_files):
        rel = os.path.relpath(dae, args.input)
        out = os.path.join(args.output, rel.replace(".dae", ".glb"))

        if os.path.exists(out):
            ok += 1
            continue

        if convert_one(dae, out):
            ok += 1
            if ok % 50 == 0:
                print(f"  [{i+1}/{len(dae_files)}] {ok} converted, {fail} failed")
        else:
            fail += 1

    print(f"\nDone: {ok} converted, {fail} failed, {len(dae_files)} processed, {total} total")


if __name__ == "__main__":
    main()
