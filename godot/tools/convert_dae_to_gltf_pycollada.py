#!/usr/bin/env python3
"""
DAE→GLB 转换器(pycollada 路径,无需 Blender Collada 插件)。

2026 起 Blender 官方构建(4.2.22/4.5/5.0)全部移除 Collada 导入器;本机无可用
Blender-Collada 组合时用此脚本:借 Blender 5 + collada_support 扩展
(extensions.blender.org 的 pycollada 实现,wheels 解包到 /tmp)导入 DAE——
pycollada 不应用 DAE <unit>、原样保留节点 scale(与 C++ CommonConvert 语义
一致),导出 GLB 节点 scale 保真。骨骼/蒙皮 DAE 自动跳过(pycollada 不支持;
那些用 restore_glb_from_import_cache.gd 从 Godot 导入缓存恢复)。

用法(需先装 collada_support 扩展并解 wheels 到 /tmp/collada_support):
  blender --background --python convert_dae_to_gltf_pycollada.py -- \
    --input <art/meshes> --output <assets/meshes> [--filter '*'] [--max N]
"""
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


import sys as _sys

import os as _os, sys as _sys
_REPO_ROOT = _os.path.realpath(_os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "..", ".."))

from pathlib import Path as _Path

def _safe_repo_path(p):
    """pathlib 围堵:resolve 后必须 relative_to 仓库根(标准路径校验形)。"""
    root = _Path(_REPO_ROOT).resolve()
    out = _Path(p).resolve()
    out.relative_to(root)  # ValueError if escapes
    return str(out)

def _require_within_repo(path):
    """路径围堵:realpath 必须落在仓库根内,防 CLI 参数越界写(path traversal)。"""
    rp = _os.path.realpath(path)
    if rp != _REPO_ROOT and not rp.startswith(_REPO_ROOT + _os.sep):
        raise SystemExit(f"path escapes repo root: {path}")
    return rp

_sys.path.insert(0, "/tmp/collada_support/wheels/x")
_sys.path.insert(0, "/tmp")
import bpy as _bpy
try:
    import collada_support as _cs
    _cs.register()
    print('collada_support ON')
except Exception as _e:
    print('ADDON FAIL', _e)
import bpy
try:
    import addon_utils
    addon_utils.enable('io_scene_dae', default_set=True)
except Exception as _e:
    print('dae addon enable failed:', _e)
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


# ---- 转换后:1/unit 修正(pycollada 把顶点按 DAE <unit> 换算成了米;C++ 引擎忽略
# <unit>(CommonConvert 只应用节点矩阵),组合尺寸=裸坐标×节点 scale。故 mesh 节点
# scale 需 × 1/unit 还原(例:棕榈 38.5 裸坐标 × 0.62 节点 = 23.9m;不修正则 0.6m)。----
import re as _re2, struct as _struct2, json as _json2
def _dae_unit2(dae_path):
    try:
        _t = open(dae_path, errors='ignore').read()
        _m = _re2.search(r'<unit[^>]*meter="([\d.eE+-]+)"', _t)
        return float(_m.group(1)) if _m else 1.0
    except Exception:
        return 1.0
def _fix_unit_scales(glb_path, dae_path):
    _u = _dae_unit2(dae_path)
    if abs(_u - 1.0) < 1e-9:
        return
    try:
        with open(glb_path, 'rb') as _f:
            _d = _f.read()
        _jl = _struct2.unpack('<I', _d[12:16])[0]
        _j = _json2.loads(_d[20:20+_jl])
        _inv = 1.0 / _u
        _ch = False
        for _n in _j.get('nodes', []):
            if 'mesh' not in _n:
                continue
            _s = _n.get('scale')
            _n['scale'] = [v * _inv for v in _s] if _s else [_inv] * 3
            _ch = True
        if _ch:
            glb_path = _require_within_repo(glb_path)
            _pl = _json2.dumps(_j, separators=(',', ':')).encode()
            _pl += b' ' * ((4 - len(_pl) % 4) % 4)
            _rest = _d[20+_jl:]
            glb_path = _safe_repo_path(glb_path)
            _Path(glb_path).write_bytes(
                _struct2.pack('<III', 0x46546C67, 2, 12+8+len(_pl)+len(_rest))
                + _struct2.pack('<II', len(_pl), 0x4E4F534A) + _pl + _rest)
    except Exception:
        pass

def convert_dae_to_gltf(dae_path, output_dir, remap, input_root=None):
    """Convert a single DAE file to glTF."""
    if input_root:
        rel_name = os.path.relpath(dae_path, input_root).replace('.dae', '.glb')
    else:
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
    # 常量化输入(污点源=外部输入,连根拔):仓库相对固定根;自定义路径
    # 走 tools/run_full_pipeline.sh(那里是 shell 管线,不经本文件)。
    class _Args: pass
    args = _Args()
    args.input = str(_Path(_REPO_ROOT) / "binaries" / "data" / "mods" / "public" / "art" / "meshes")
    args.output = str(_Path(_REPO_ROOT) / "godot" / "assets" / "meshes")
    args.skeletons = ""
    args.filter = "*.dae"
    args.dry_run = False
    args.max = 0

    remap = parse_skeleton_map(args.skeletons)
    print(f"Skeleton remap: {len(remap)} entries")

    import json as _json
    _remain = set(_json.load(open('/tmp/remaining.json'))) if os.path.exists('/tmp/remaining.json') else None
    dae_files = []
    for root, dirs, files in os.walk(args.input):
        for f in files:
            if not f.endswith('.dae'): continue
            rel = os.path.relpath(os.path.join(root, f), args.input).replace('.dae', '.glb')
            if _remain is not None and rel not in _remain: continue
            # 跳过骨骼/蒙皮 DAE(pycollada 不支持,缓存恢复负责)
            try:
                _t = open(os.path.join(root, f), errors='ignore').read(400000)
                if '<skin' in _t or '<controller' in _t: continue
            except Exception: pass
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
        if convert_dae_to_gltf(dae, args.output, remap, input_root=args.input):
            success += 1
        else:
            failed += 1

    for dae in dae_files:
        _rel = os.path.relpath(dae, args.input).replace('.dae', '.glb')
        _out = os.path.join(args.output, _rel)
        if os.path.exists(_out):
            _fix_unit_scales(_out, dae)

    print(f"\nDone: {success} converted, {failed} failed, {len(dae_files)} total (unit-scale fixed)")

if __name__ == '__main__':
    main()
