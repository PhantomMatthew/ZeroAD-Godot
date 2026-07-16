#!/usr/bin/env python3
"""
0 A.D. skeletal mesh + head props + animations -> single animated glTF.
Blender 4.2 LTS only (Collada import removed in 5.0).

Usage:
    blender --background --python build_animated_unit.py -- \
        --mesh   <body.dae> \
        --output <out.glb> \
        --prop   head=<head.dae> \
        --anim   walk=<walk.dae> --anim idle=<idle.dae>
"""

import bpy
import sys
import argparse


def clear_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def import_collada(path):
    bpy.ops.wm.collada_import(filepath=path)


def find_armature():
    for obj in bpy.data.objects:
        if obj.type == "ARMATURE":
            return obj
    return None


def find_bone(armature, name):
    if armature and armature.data:
        for bone in armature.data.bones:
            if bone.name == name or bone.name.lower() == name.lower():
                return bone.name
    return None


def import_body(mesh_path):
    import_collada(mesh_path)
    armature = find_armature()
    if armature is None:
        print(f"  FAIL: no armature in {mesh_path}")
        return None
    return armature


def attach_prop(armature, bone_name, prop_path):
    objs_before = set(bpy.data.objects.keys())
    import_collada(prop_path)
    new_objs = [bpy.data.objects[k] for k in bpy.data.objects.keys()
                if k not in objs_before]

    actual_bone = find_bone(armature, bone_name)
    if actual_bone is None:
        print(f"  WARN: bone '{bone_name}' not found, using offset")
        actual_bone = bone_name

    for obj in new_objs:
        if obj.type == "MESH":
            obj.parent = armature
            obj.parent_type = "BONE"
            obj.parent_bone = actual_bone
            mat = obj.matrix_world.copy()
            obj.matrix_parent_inverse = armature.matrix_world.inverted()
            print(f"  prop '{obj.name}' -> bone '{actual_bone}'")
        elif obj.type == "ARMATURE":
            for child in obj.children:
                child.parent = armature
                child.parent_type = "BONE"
                child.parent_bone = actual_bone
                child.matrix_parent_inverse = armature.matrix_world.inverted()
                print(f"  prop '{child.name}' -> bone '{actual_bone}' (from sub-armature)")
            bpy.data.objects.remove(obj, do_unlink=True)

    # Merge the armature's animation action reference
    if not armature.animation_data:
        armature.animation_data_create()


def extract_action(anim_path, anim_name):
    actions_before = set(bpy.data.actions.keys())
    import_collada(anim_path)
    new_actions = [bpy.data.actions[k] for k in bpy.data.actions.keys()
                   if k not in actions_before]

    action = new_actions[0] if new_actions else None
    if action:
        action.name = anim_name
        action.use_fake_user = True

    imported_arms = [o for o in bpy.data.objects
                     if o.type == "ARMATURE" and o.animation_data
                     and o.animation_data.action in new_actions]
    for obj in imported_arms:
        bpy.data.objects.remove(obj, do_unlink=True)

    # Also clean up stray meshes from the anim file
    for obj in list(bpy.data.objects):
        if obj.type == "MESH" and not obj.users_scene:
            bpy.data.objects.remove(obj, do_unlink=True)

    return action


def build(mesh_path, props, anims, output_path):
    clear_scene()

    armature = import_body(mesh_path)
    if armature is None:
        return False

    for bone_name, prop_path in props.items():
        attach_prop(armature, bone_name, prop_path)

    if not armature.animation_data:
        armature.animation_data_create()

    actions = []
    for name, path in anims.items():
        act = extract_action(path, name)
        if act:
            actions.append(act)
            print(f"  anim: {name} ({len(act.fcurves)} fcurves)")

    if actions:
        armature.animation_data.action = actions[0]
        for act in actions:
            track = armature.animation_data.nla_tracks.new()
            track.name = act.name
            track.strips.new(act.name, int(act.frame_range[0]), act)
        armature.animation_data.action = None

    bpy.ops.object.select_all(action="DESELECT")

    bpy.ops.export_scene.gltf(
        filepath=output_path,
        export_format="GLB",
        export_animations=True,
        export_animation_mode="ACTIONS",
        export_nla_strips=True,
        export_skins=True,
        export_yup=True,
    )
    print(f"  OK: {output_path} ({len(actions)} animations, {len(props)} props)")
    return True


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser()
    parser.add_argument("--mesh", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--prop", action="append", default=[])
    parser.add_argument("--anim", action="append", default=[])
    args = parser.parse_args(argv)

    props = {}
    for spec in args.prop:
        if "=" in spec:
            bone, path = spec.split("=", 1)
            props[bone.strip()] = path.strip()

    anims = {}
    for spec in args.anim:
        if "=" in spec:
            name, path = spec.split("=", 1)
            anims[name.strip()] = path.strip()

    ok = build(args.mesh, props, anims, args.output)
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
