import bpy
import sys
import os

def diagnose(dae):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    try:
        bpy.ops.wm.collada_import(filepath=dae)
    except Exception as e:
        print(f"IMPORT ERROR: {e}")
        return
    objs = list(bpy.data.objects)
    print(f"imported {len(objs)} objects from {os.path.basename(dae)}")
    for o in objs:
        nverts = len(o.data.vertices) if o.type == "MESH" and o.data else 0
        print(f"  {o.type}: {o.name}  verts={nverts} scale={tuple(round(s,4) for s in o.scale)}")
    # armatures
    arms = [o for o in objs if o.type == "ARMATURE"]
    meshes = [o for o in objs if o.type == "MESH"]
    print(f"  -> {len(arms)} armatures, {len(meshes)} meshes")
    # try export
    out = "/tmp/diag_out.glb"
    try:
        bpy.ops.export_scene.gltf(filepath=out, export_format="GLB", export_yup=True,
            export_apply=False, export_skins=True, export_animations=True, export_animation_mode="ACTIONS")
        print(f"  exported -> {out} size={os.path.getsize(out)}")
    except Exception as e:
        print(f"EXPORT ERROR: {e}")

argv = sys.argv[sys.argv.index("--")+1:] if "--" in sys.argv else []
if argv:
    diagnose(argv[0])
