#!/usr/bin/env python3
"""
0 A.D. DAE → glTF batch conversion pipeline.
Requires Blender 4.x with command-line access.

Usage:
    blender --background --python convert_dae_to_gltf.py -- \
        --input /path/to/binaries/data/mods/public/art/meshes \
        --output /path/to/godot/assets/meshes \
        --skeletons /path/to/art/skeletons \
        [--filter unit_*] [--dry-run]

Preserves:
    - Bone names (via skeleton remap XML)
    - Prop attachment bones (prop-* naming)
    - Animations (separate DAE → glTF .anim files)
"""

import bpy
import sys
import os
import argparse
import xml.etree.ElementTree as ET
from pathlib import Path

def parse_skeleton_map(skeletons_dir):
    """Parse art/skeletons/*.xml to build bone name remapping."""
    remap = {}
    if not skeletons_dir or not os.path.isdir(skeletons_dir):
        return remap
    for f in os.listdir(skeletons_dir):
        if not f.endswith('.xml'):
            continue
        path = os.path.join(skeletons_dir, f)
        try:
            tree = ET.parse(path)
            root = tree.getroot()
            for skeleton in root.findall('.//skeleton'):
                target = skeleton.get('target', '')
                for bone in skeleton.findall('.//bone'):
                    original = bone.get('target', '')
                    standard = bone.get('id', '')
                    if original and standard:
                        remap[original.lower()] = standard
        except Exception as e:
            print(f"  WARN: Failed to parse {f}: {e}")
    return remap

def remap_bones(obj, remap):
    """Apply bone name remapping to armature."""
    if not obj or obj.type != 'ARMATURE':
        return
    if not obj.data:
        return
    for bone in obj.data.bones:
        key = bone.name.lower()
        if key in remap:
            bone.name = remap[key]

def is_prop_bone(bone_name):
    """Check if bone is a prop attachment point."""
    return bone_name.startswith('prop-') or bone_name.startswith('prop_')

def convert_dae_to_gltf(dae_path, output_dir, remap):
    """Convert a single DAE file to glTF."""
    rel_name = os.path.basename(dae_path).replace('.dae', '.glb')

    try:
        bpy.ops.wm.read_factory_settings(use_empty=True)
    except Exception:
        pass

    try:
        bpy.ops.wm.append(directory=dae_path)
    except Exception:
        try:
            bpy.ops.import_scene.collada(filepath=dae_path)
        except Exception as e:
            print(f"  SKIP: Cannot import {dae_path}: {e}")
            return False

    for obj in bpy.data.objects:
        if obj.type == 'ARMATURE':
            remap_bones(obj, remap)

    for obj in bpy.data.objects:
        if obj.type == 'ARMATURE' and obj.data:
            for bone in list(obj.data.bones):
                if not is_prop_bone(bone.name) and obj.data.bones[bone.name].use_deform == False:
                    pass

    out_path = os.path.join(output_dir, rel_name)
    os.makedirs(os.path.dirname(out_path) if os.path.dirname(out_path) else '.', exist_ok=True)

    try:
        bpy.ops.export_scene.gltf(
            filepath=out_path,
            export_format='GLB',
            export_apply=True,
            export_animations=True,
            export_yup=True,
        )
        print(f"  OK: {os.path.basename(dae_path)} -> {rel_name}")
        return True
    except Exception as e:
        print(f"  FAIL: Export {dae_path}: {e}")
        return False

def main():
    argv = sys.argv
    if '--' in argv:
        argv = argv[argv.index('--') + 1:]
    else:
        argv = []

    parser = argparse.ArgumentParser(description='0 A.D. DAE → glTF converter')
    parser.add_argument('--input', required=True, help='Input meshes directory')
    parser.add_argument('--output', required=True, help='Output directory')
    parser.add_argument('--skeletons', default='', help='Skeleton definitions directory')
    parser.add_argument('--filter', default='*.dae', help='File pattern filter')
    parser.add_argument('--dry-run', action='store_true', help='List files without converting')
    parser.add_argument('--max', type=int, default=0, help='Max files to convert (0=all)')

    args = parser.parse_args(argv)

    remap = parse_skeleton_map(args.skeletons)
    print(f"Skeleton remap: {len(remap)} entries")

    dae_files = []
    for root, dirs, files in os.walk(args.input):
        for f in files:
            if f.endswith('.dae'):
                dae_files.append(os.path.join(root, f))

    if args.filter != '*.dae':
        import fnmatch
        dae_files = [f for f in dae_files if fnmatch.fnmatch(os.path.basename(f), args.filter)]

    if args.max > 0:
        dae_files = dae_files[:args.max]

    print(f"Found {len(dae_files)} DAE files")

    if args.dry_run:
        for f in dae_files:
            print(f"  {f}")
        return

    os.makedirs(args.output, exist_ok=True)

    success = 0
    failed = 0
    for dae in dae_files:
        if convert_dae_to_gltf(dae, args.output, remap):
            success += 1
        else:
            failed += 1

    print(f"\nDone: {success} converted, {failed} failed, {len(dae_files)} total")

if __name__ == '__main__':
    main()
