"""蒙皮 GLB 单位空间审计(只读,不改文件).

背景:C++ 忽略 DAE `<unit meter>`,裸坐标 × bind_shape 即游戏米;Blender 转换时
遵守 unit,导致缩放因子随机寄存在顶点 / 骨骼 rest / 逆绑定矩阵(IBM) / 节点缩放
里。本工具对每个蒙皮 GLB 回答三个问题:

1. 实际渲染多大?——用蒙皮方程 v' = Σ w·W_joint·IBM·v 在 rest 位姿下求世界跨度
   (已用 deer_mesh 4.36m 验证与游戏一致)。
2. 应该是多大?——同源 DAE 的裸顶点跨度 × bind_shape 缩放(C++ 语义)。
3. 缩放寄存在哪?——顶点量级 / 骨骼 rest 量级 / IBM 中位缩放 / 节点累计缩放,
   据此分类:canonical(全米制、IBM≈1、无节点缩放)、ibm-bridged、node-scaled、
   broken(实测与目标偏差 > 15%)。

动画段做自洽检查:位移轨道最大模长 vs 本文件骨骼 rest 最大模长,比值远离 1
时报 suspect(不区分谁对谁错,只列出来供归一化阶段对照网格骨架处理)。

用法: python3 audit_glb_unit_spaces.py [--meshes-root PATH] [--anims-root PATH]
       [--dae-root PATH] [--report PATH] [--full]
"""
from __future__ import annotations

import argparse
import json
import re
import struct
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

_NCOMP = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}
_FMT = {5126: ("f", 4), 5123: ("H", 2), 5121: ("B", 1), 5125: ("I", 4)}
OK_TOL = 0.15
SUBSAMPLE = 1500


def read_glb(path: Path) -> tuple[dict, bytes, bytes]:
    data = path.read_bytes()
    magic, _version, _length = struct.unpack_from("<III", data, 0)
    if magic != 0x46546C67:
        raise ValueError("not a GLB")
    json_len, json_type = struct.unpack_from("<II", data, 12)
    if json_type != 0x4E4F534A:
        raise ValueError("first chunk is not JSON")
    rest = data[20 + json_len :]
    return json.loads(data[20 : 20 + json_len]), rest, rest[8:]


def accessor_rows(gltf: dict, bin_: bytes, idx: int) -> list[tuple[float, ...]]:
    acc = gltf["accessors"][idx]
    bv = gltf["bufferViews"][acc["bufferView"]]
    off = bv.get("byteOffset", 0) + acc.get("byteOffset", 0)
    ncomp = _NCOMP[acc["type"]]
    fmt, size = _FMT[acc["componentType"]]
    stride = bv.get("byteStride") or ncomp * size
    return [struct.unpack_from(f"<{ncomp}{fmt}", bin_, off + i * stride)
            for i in range(acc["count"])]


def matmul(a: list[list[float]], b: list[list[float]]) -> list[list[float]]:
    return [[sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


def node_local(node: dict) -> list[list[float]]:
    if "matrix" in node:
        m = node["matrix"]
        return [[m[0], m[4], m[8], m[12]], [m[1], m[5], m[9], m[13]],
                [m[2], m[6], m[10], m[14]], [m[3], m[7], m[11], m[15]]]
    t = node.get("translation", [0, 0, 0])
    x, y, z, w = node.get("rotation", [0, 0, 0, 1])
    s = node.get("scale", [1, 1, 1])
    r = [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
         [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
         [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]
    return [[r[i][j] * s[j] for j in range(3)] + [t[i]] for i in range(3)] + [[0, 0, 0, 1]]


def mat_to_row_major(m: list[float]) -> list[list[float]]:
    return [[m[0], m[4], m[8], m[12]], [m[1], m[5], m[9], m[13]],
            [m[2], m[6], m[10], m[14]], [m[3], m[7], m[11], m[15]]]


def skinned_audit(gltf: dict, bin_: bytes) -> dict | None:
    """Rest-pose world span + scale-layout metrics for a skinned GLB."""
    if not gltf.get("skins"):
        return None
    nodes = gltf["nodes"]
    parent: dict[int, int] = {}
    for i, n in enumerate(nodes):
        for c in n.get("children", []):
            parent[c] = i
    cache: dict[int, list[list[float]]] = {}

    def world(i: int) -> list[list[float]]:
        if i not in cache:
            m = node_local(nodes[i])
            p = parent.get(i)
            cache[i] = m if p is None else matmul(world(p), m)
        return cache[i]

    skin = gltf["skins"][0]
    joints = skin["joints"]
    ibm_idx = skin.get("inverseBindMatrices")
    if ibm_idx is None:
        return None
    ibm = [mat_to_row_major(list(m)) for m in accessor_rows(gltf, bin_, ibm_idx)]
    combo = [matmul(world(j), ibm[k]) for k, j in enumerate(joints)]

    prim = None
    mesh_node = None
    for i, n in enumerate(nodes):
        if n.get("mesh") is not None and n.get("skin") is not None:
            prim = gltf["meshes"][n["mesh"]]["primitives"][0]
            mesh_node = i
            break
    if prim is None:
        return None
    verts = accessor_rows(gltf, bin_, prim["attributes"]["POSITION"])
    jts = accessor_rows(gltf, bin_, prim["attributes"]["JOINTS_0"])
    wts = accessor_rows(gltf, bin_, prim["attributes"]["WEIGHTS_0"])
    wcomp = gltf["accessors"][prim["attributes"]["WEIGHTS_0"]]["componentType"]
    wnorm = 255.0 if wcomp == 5121 else (65535.0 if wcomp == 5123 else 1.0)

    step = max(1, len(verts) // SUBSAMPLE)
    lo = [float("inf")] * 3
    hi = [float("-inf")] * 3
    for vi in range(0, len(verts), step):
        v = verts[vi]
        p = [0.0, 0.0, 0.0]
        for k in range(4):
            w = wts[vi][k] / wnorm
            if w == 0:
                continue
            m = combo[int(jts[vi][k])]
            for axis in range(3):
                p[axis] += w * (m[axis][0] * v[0] + m[axis][1] * v[1]
                                + m[axis][2] * v[2] + m[axis][3])
        for axis in range(3):
            lo[axis] = min(lo[axis], p[axis])
            hi[axis] = max(hi[axis], p[axis])
    span = max(hi[axis] - lo[axis] for axis in range(3))

    vspan = 0.0
    acc = gltf["accessors"][prim["attributes"]["POSITION"]]
    if acc.get("min") and acc.get("max"):
        vspan = max(abs(acc["max"][i] - acc["min"][i]) for i in range(3))
    ibm_scales = sorted(
        (m[0][0] ** 2 + m[1][0] ** 2 + m[2][0] ** 2) ** 0.5 for m in ibm)
    rest_max = 0.0
    for j in joints:
        t = nodes[j].get("translation", [0, 0, 0])
        rest_max = max(rest_max, (t[0] ** 2 + t[1] ** 2 + t[2] ** 2) ** 0.5)
    node_scale_max = 1.0
    for n in nodes:
        s = n.get("scale")
        if s:
            node_scale_max = max(node_scale_max, abs(s[0]), 1.0 / max(abs(s[0]), 1e-9))
    # glTF 规范忽略蒙皮网格节点的自身变换;2026-08-22 引擎实测(船帆网格节点带
    # 非均匀缩放 1.186/0.897/1.0,渲染仍精确等于 span_spec=27.363=DAE 目标)确认
    # Godot 4.7 遵循规范。分类一律按 span_spec;"span"(叠乘网格节点缩放)仅作
    # 诊断列保留。关节祖先的缩放已含在 span_spec 里(它在 W_joint 中)。
    mesh_world = world(mesh_node)
    mesh_world_scale = (mesh_world[0][0] ** 2 + mesh_world[1][0] ** 2
                        + mesh_world[2][0] ** 2) ** 0.5
    return {
        "span": round(span * mesh_world_scale, 3),
        "span_spec": round(span, 3),
        "vert_span": round(vspan, 4),
        "ibm_scale": round(ibm_scales[len(ibm_scales) // 2], 4),
        "rest_max": round(rest_max, 3),
        "node_scale_max": round(node_scale_max, 4),
        "mesh_node_scale": round(mesh_world_scale, 4),
    }


def dae_target(dae_path: Path) -> float | None:
    """bind_shape 矩阵作用于裸包围盒后的最大轴向跨度 —— C++ 尺寸。
    必须按全矩阵逐轴计算:船帆的 bind_shape 是非均匀的(0.843/1.0/1.115),
    用"裸最大轴 × bind 最大列"会把 27.4m 误算成 30.5m(2026-08-22 误报教训)。
    多 geometry 文件按几何体分组各自计算再取 max——跨几何体混轴会虚增
    (POSITION 语义经 vertices → source 正确解析,不靠数组名猜测)。"""
    try:
        tree = ET.parse(dae_path)
    except (OSError, ET.ParseError):
        return None
    ns = "{http://www.collada.org/2005/11/COLLADASchema}"
    root_el = tree.getroot()

    def floats(el: ET.Element | None) -> list[float]:
        if el is None or not el.text:
            return []
        return [float(t) for t in el.text.split()]

    bind = [1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0]
    bs = root_el.find(f".//{ns}bind_shape_matrix")
    if bs is not None and bs.text:
        bind = [float(t) for t in bs.text.split()]

    best = 0.0
    for geom in root_el.iter(f"{ns}geometry"):
        mesh = geom.find(f"{ns}mesh")
        if mesh is None:
            continue
        # vertices → POSITION 语义 → source → float_array
        pos_source = None
        for vtx in mesh.findall(f"{ns}vertices"):
            for inp in vtx.findall(f"{ns}input"):
                if inp.get("semantic") == "POSITION":
                    pos_source = inp.get("source", "").lstrip("#")
        if pos_source is None:
            continue
        exts = [0.0, 0.0, 0.0]
        for src in mesh.findall(f"{ns}source"):
            if src.get("id") != pos_source:
                continue
            fa = src.find(f"{ns}float_array")
            nums = floats(fa)
            if len(nums) < 3:
                continue
            xs, ys, zs = nums[0::3], nums[1::3], nums[2::3]
            exts = [max(xs) - min(xs), max(ys) - min(ys), max(zs) - min(zs)]
            break
        if max(exts) <= 0.0:
            continue
        spans = [sum(abs(bind[r * 4 + c]) * exts[c] for c in range(3)) for r in range(3)]
        best = max(best, max(spans))
    return best if best > 0.0 else None


def classify(info: dict, target: float | None) -> str:
    if target is None:
        return "no-dae"
    if target > 1e-6 and abs(info["span_spec"] - target) > OK_TOL * target:
        return "broken"
    if abs(info["ibm_scale"] - 1.0) > 0.05:
        return "ibm-bridged"
    if info["node_scale_max"] > 1.05:
        return "node-scaled"
    if info["vert_span"] > 1e-6 and abs(info["vert_span"] - info["span_spec"]) > 0.05 * info["span_spec"]:
        return "mixed"
    return "canonical"


def audit_anims(anims_root: Path) -> list[dict]:
    rows = []
    for glb_path in sorted(anims_root.rglob("*.glb")):
        try:
            gltf, _rest, bin_ = read_glb(glb_path)
            if not gltf.get("skins"):
                continue
            rest_max = 0.0
            for j in gltf["skins"][0]["joints"]:
                t = gltf["nodes"][j].get("translation", [0, 0, 0])
                rest_max = max(rest_max, (t[0] ** 2 + t[1] ** 2 + t[2] ** 2) ** 0.5)
            key_max = 0.0
            key_bone = ""
            for anim in gltf.get("animations", []):
                for ch in anim.get("channels", []):
                    if ch.get("target", {}).get("path") != "translation":
                        continue
                    smp = anim["samplers"][ch["sampler"]]
                    for row in accessor_rows(gltf, bin_, smp["output"]):
                        mag = (row[0] ** 2 + row[1] ** 2 + row[2] ** 2) ** 0.5
                        if mag > key_max:
                            key_max = mag
                            key_bone = gltf["nodes"][ch["target"]["node"]].get("name", "")
            if rest_max > 1e-4 and key_max > 1e-4:
                ratio = key_max / rest_max
                if ratio < 0.5 or ratio > 2.0:
                    rows.append({"file": str(glb_path), "rest_max": round(rest_max, 3),
                                 "key_max": round(key_max, 3), "key_bone": key_bone,
                                 "ratio": round(ratio, 3)})
        except (ValueError, OSError, KeyError, IndexError, struct.error) as exc:
            rows.append({"file": str(glb_path), "error": str(exc)})
    return rows


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--meshes-root", default="godot/assets/meshes")
    parser.add_argument("--anims-root", default="godot/assets/animations")
    parser.add_argument("--dae-root", default="binaries/data/mods/public/art/meshes")
    parser.add_argument("--report", default="/tmp/glb_unit_space_audit.json")
    parser.add_argument("--full", action="store_true", help="also audit animations")
    args = parser.parse_args()

    meshes_root = Path(args.meshes_root)
    dae_root = Path(args.dae_root)
    report: dict[str, list[dict]] = {}
    for glb_path in sorted(meshes_root.rglob("*.glb")):
        rel = glb_path.relative_to(meshes_root)
        try:
            gltf, _rest, bin_ = read_glb(glb_path)
            info = skinned_audit(gltf, bin_)
        except (ValueError, OSError, KeyError, IndexError, struct.error) as exc:
            report.setdefault("error", []).append({"file": str(rel), "error": str(exc)})
            continue
        if info is None:
            continue
        target = dae_target(dae_root / rel.with_suffix(".dae"))
        info["target"] = round(target, 3) if target is not None else None
        verdict = classify(info, target)
        report.setdefault(verdict, []).append({"file": str(rel), **info})

    for verdict in sorted(report):
        print(f"{verdict}: {len(report[verdict])}")
    if args.full:
        suspects = audit_anims(Path(args.anims_root))
        report["anim-suspect"] = suspects
        print(f"anim-suspect: {len(suspects)}")
    Path(args.report).write_text(json.dumps(report, indent=1, ensure_ascii=False))
    print(f"report: {args.report}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
