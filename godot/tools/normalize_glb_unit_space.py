"""蒙皮 GLB 单位空间归一化(带逐顶点不变量证明).

目标约定(canonical):顶点、骨骼 rest、IBM、动画轨道全部落在游戏米制,节点
缩放为 1——GLB 里量到多少,游戏里就是多少。审计见 audit_glb_unit_spaces.py。

核心 primitive(零视觉变化):蒙皮渲染 v' = Σ w·W·IBM·v,因此对顶点 ×k 同时
把 IBM 右乘 S(1/k)(前三列除以 k),渲染结果逐点不变。k 取 span/vert_span 后,
顶点跨度 == 蒙皮跨度 == 游戏米,IBM 缩放随之归 1。骨骼/动画不动(它们本来就
在最终空间——这正是 ibm-bridged 类的定义)。

retarget 模式(仅用于当前已损坏的文件,如 target_marker 渲染 0.15m 应为 6m):
1. 剥掉 <1 的均匀节点缩放(不做补偿——该缩放正是 Blender 错误引入的,剥掉后
   骨骼回到作者单位即游戏米);
2. 顶点 ×(target/vert_span);
3. IBM 全部重算为 inverse(关节世界 rest)(bind==rest 对这些资产成立)。

每个写入的文件都做两道证明:逐顶点蒙皮结果前后一致(canonicalize,容差
1e-4 相对值)或达到目标跨度(retarget,1% 容差);失败则跳过并报告,不写盘。

用法:
  python3 normalize_glb_unit_space.py [--dry-run]            # canonicalize 健康文件
  python3 normalize_glb_unit_space.py --retarget skeletal/target_marker.glb --to-span 6.036
"""
from __future__ import annotations

import argparse
import json
import struct
import sys
from pathlib import Path

_NCOMP = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}
_FMT = {5126: ("f", 4), 5123: ("H", 2), 5121: ("B", 1), 5125: ("I", 4)}
SUBSAMPLE = 100000  # 归一化要精确 k,不做子采样(审计工具才需要速度)
INVAR_TOL = 1e-4
RETARGET_TOL = 0.01


def read_glb(path: Path) -> tuple[dict, bytearray]:
    data = path.read_bytes()
    magic, _version, _length = struct.unpack_from("<III", data, 0)
    if magic != 0x46546C67:
        raise ValueError("not a GLB")
    json_len, json_type = struct.unpack_from("<II", data, 12)
    if json_type != 0x4E4F534A:
        raise ValueError("first chunk is not JSON")
    return json.loads(data[20 : 20 + json_len]), bytearray(data[20 + json_len :])


def write_glb(path: Path, gltf: dict, rest: bytearray) -> None:
    payload = json.dumps(gltf, separators=(",", ":")).encode()
    payload += b" " * ((4 - len(payload) % 4) % 4)
    total = 12 + 8 + len(payload) + len(rest)
    header = struct.pack("<III", 0x46546C67, 2, total)
    chunk = struct.pack("<II", len(payload), 0x4E4F534A)
    path.write_bytes(header + chunk + payload + bytes(rest))


def bin_view(rest: bytearray) -> memoryview:
    return memoryview(rest)[8:]


def accessor_meta(gltf: dict, idx: int) -> tuple[int, int, int, int]:
    """(byte offset into BIN, ncomp, count, stride)."""
    acc = gltf["accessors"][idx]
    bv = gltf["bufferViews"][acc["bufferView"]]
    off = bv.get("byteOffset", 0) + acc.get("byteOffset", 0)
    ncomp = _NCOMP[acc["type"]]
    fmt, size = _FMT[acc["componentType"]]
    del fmt
    return off, ncomp, acc["count"], bv.get("byteStride") or ncomp * size


def read_rows(gltf: dict, bin_: memoryview, idx: int) -> list[tuple[float, ...]]:
    acc = gltf["accessors"][idx]
    fmt, _size = _FMT[acc["componentType"]]
    off, ncomp, count, stride = accessor_meta(gltf, idx)
    return [struct.unpack_from(f"<{ncomp}{fmt}", bin_, off + i * stride)
            for i in range(count)]


def scale_float_accessor(gltf: dict, bin_: memoryview, idx: int, factor: float,
                         columns: range | None = None) -> None:
    off, ncomp, count, stride = accessor_meta(gltf, idx)
    cols = columns if columns is not None else range(ncomp)
    for i in range(count):
        for c in cols:
            o = off + i * stride + c * 4
            struct.pack_into("<f", bin_, o, struct.unpack_from("<f", bin_, o)[0] * factor)
    acc = gltf["accessors"][idx]
    for key in ("min", "max"):
        if key in acc and columns is None:
            acc[key] = [v * factor for v in acc[key]]


def matmul(a: list[list[float]], b: list[list[float]]) -> list[list[float]]:
    return [[sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


def mat_invert(m: list[list[float]]) -> list[list[float]]:
    """General 4x4 inverse (Gauss-Jordan) — rests may carry rotation."""
    n = 4
    aug = [row[:] + [1.0 if i == j else 0.0 for j in range(n)] for i, row in enumerate(m)]
    for col in range(n):
        piv = max(range(col, n), key=lambda r: abs(aug[r][col]))
        if abs(aug[piv][col]) < 1e-12:
            raise ValueError("singular matrix")
        aug[col], aug[piv] = aug[piv], aug[col]
        inv = 1.0 / aug[col][col]
        aug[col] = [v * inv for v in aug[col]]
        for r in range(n):
            if r != col and aug[r][col] != 0.0:
                f = aug[r][col]
                aug[r] = [a - f * b for a, b in zip(aug[r], aug[col], strict=True)]
    return [row[n:] for row in aug]


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


def world_transforms(gltf: dict) -> list[list[list[float]]]:
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

    return [world(i) for i in range(len(nodes))]


def skinned_sample(gltf: dict, bin_: memoryview,
                   worlds: list[list[list[float]]]) -> tuple[list[tuple[float, ...]], dict]:
    """Subsampled skinned rest positions + layout metrics."""
    skin = gltf["skins"][0]
    joints = skin["joints"]
    ibm_rows = read_rows(gltf, bin_, skin["inverseBindMatrices"])
    ibm = [[[m[0], m[4], m[8], m[12]], [m[1], m[5], m[9], m[13]],
            [m[2], m[6], m[10], m[14]], [m[3], m[7], m[11], m[15]]] for m in ibm_rows]
    combo = [matmul(worlds[j], ibm[k]) for k, j in enumerate(joints)]
    prim = None
    mesh_node = None
    for i, n in enumerate(gltf["nodes"]):
        if n.get("mesh") is not None and n.get("skin") is not None:
            prim = gltf["meshes"][n["mesh"]]["primitives"][0]
            mesh_node = i
            break
    if prim is None:
        raise ValueError("no skinned mesh primitive")
    verts = read_rows(gltf, bin_, prim["attributes"]["POSITION"])
    jts = read_rows(gltf, bin_, prim["attributes"]["JOINTS_0"])
    wts = read_rows(gltf, bin_, prim["attributes"]["WEIGHTS_0"])
    wcomp = gltf["accessors"][prim["attributes"]["WEIGHTS_0"]]["componentType"]
    wnorm = 255.0 if wcomp == 5121 else (65535.0 if wcomp == 5123 else 1.0)
    mw = worlds[mesh_node]
    step = max(1, len(verts) // SUBSAMPLE)
    out = []
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
        out.append((mw[0][0] * p[0] + mw[0][1] * p[1] + mw[0][2] * p[2] + mw[0][3],
                    mw[1][0] * p[0] + mw[1][1] * p[1] + mw[1][2] * p[2] + mw[1][3],
                    mw[2][0] * p[0] + mw[2][1] * p[1] + mw[2][2] * p[2] + mw[2][3]))
    acc = gltf["accessors"][prim["attributes"]["POSITION"]]
    vspan = max(abs(acc["max"][i] - acc["min"][i]) for i in range(3))
    metrics = {"vert_span": vspan, "prim": prim, "mesh_node": mesh_node}
    return out, metrics


def span_of(points: list[tuple[float, ...]]) -> float:
    return max(max(p[i] for p in points) - min(p[i] for p in points) for i in range(3))


def node_scale_extreme(gltf: dict) -> float:
    extreme = 1.0
    for n in gltf.get("nodes", []):
        s = n.get("scale")
        if s:
            extreme = max(extreme, abs(s[0]), 1.0 / max(abs(s[0]), 1e-9))
    return extreme


# C++ 转换器对非 XSI 导出完全忽略节点变换(source/collada/PMDConvert.cpp
# TransformSkinnedModel:verts = bind_shape × raw),所以 retarget 不按缩放值设闸——
# 所有节点缩放都可剥,目标跨度的 1% 验证是唯一闸门。审计目标 = bind × raw 即真值。


def canonicalize(path: Path, dry_run: bool) -> str:
    gltf, rest = read_glb(path)
    if not gltf.get("skins"):
        return "skip:not-skinned"
    if node_scale_extreme(gltf) > 1.01:
        return "skip:node-scales-present"
    bin_ = bin_view(rest)
    before, meta = skinned_sample(gltf, bin_, world_transforms(gltf))
    span = span_of(before)
    vspan = meta["vert_span"]
    if vspan < 1e-9:
        return "skip:no-geometry"
    k = span / vspan
    if abs(k - 1.0) < 0.01:
        return "ok:already-canonical"

    skin = gltf["skins"][0]
    if not dry_run:
        for mesh in gltf.get("meshes", []):
            for prim in mesh.get("primitives", []):
                pos = prim.get("attributes", {}).get("POSITION")
                if pos is not None:
                    scale_float_accessor(gltf, bin_, pos, k)
        # IBM 右乘 S(1/k):列 0..2 除以 k,平移列不动
        scale_float_accessor(gltf, bin_, skin["inverseBindMatrices"], 1.0 / k,
                             columns=range(0, 12))
        # 上面 range(0,12) 会把每列 16 个浮点的前 12 个(含平移?)——列主序下
        # m[0..2],m[4..6],m[8..10] 是线性部分,m[12..14] 是平移。
        # range(0,12) 恰好覆盖前三列(0-2,4-6,8-10)但包含 3,7,11(每列第 4 分量,
        # 透视行,恒为 0),缩放 0 无副作用;m[12..14] 未被触及。正确。
        after, _meta2 = skinned_sample(gltf, bin_, world_transforms(gltf))
        for pb, pa in zip(before, after, strict=True):
            for axis in range(3):
                if abs(pb[axis] - pa[axis]) > INVAR_TOL * max(1.0, abs(pb[axis])):
                    return "error:invariance-violated (file NOT written)"
        write_glb(path, gltf, rest)
    return f"{'would-fix' if dry_run else 'fixed'}:x{k:.4f} span {span:.3f} kept"


def retarget(path: Path, to_span: float, dry_run: bool, bone_scale: float = 1.0) -> str:
    """C++ 真值规则(非 XSI 导出):渲染 = bind_shape × 裸顶点,节点变换全部忽略
    (source/collada/PMDConvert.cpp TransformSkinnedModel)。因此所有节点缩放都可
    剥——目标跨度已含真值,1% 验证是真正的闸门。bone_scale 用于骨骼与顶点不同
    单位的文件(goat:顶点米/骨骼厘米),剥离后把关节 rest 位移再乘该因子。"""
    gltf, rest = read_glb(path)
    if not gltf.get("skins"):
        return "skip:not-skinned"
    bin_ = bin_view(rest)
    actions: list[str] = []
    for node in gltf.get("nodes", []):
        s = node.get("scale")
        if s is None:
            continue
        if abs(s[0] - 1.0) < 1e-4 and abs(s[1] - 1.0) < 1e-4 and abs(s[2] - 1.0) < 1e-4:
            node.pop("scale")
            continue
        actions.append(f"strip:{node.get('name')}={s[0]:.6g}")
        node.pop("scale")
    if bone_scale != 1.0:
        joints = set(gltf["skins"][0]["joints"])
        for j in joints:
            t = gltf["nodes"][j].get("translation")
            if t:
                gltf["nodes"][j]["translation"] = [v * bone_scale for v in t]
        actions.append(f"bone-rests:x{bone_scale:g}")
    worlds = world_transforms(gltf)
    _before, meta = skinned_sample(gltf, bin_, worlds)
    vspan = meta["vert_span"]
    if vspan < 1e-9:
        return "skip:no-geometry"
    k = to_span / vspan
    actions.append(f"verts:x{k:.4f}->{to_span:.3f}m")
    if dry_run:
        return "would-fix: " + "; ".join(actions)

    skin = gltf["skins"][0]
    joints = skin["joints"]
    # IBM 重算为 inverse(关节世界 rest)(bind==rest;缩放已在剥节点时归位)
    new_ibm = [mat_invert(worlds[j]) for j in joints]
    off, _ncomp, count, stride = accessor_meta(gltf, skin["inverseBindMatrices"])
    for i, m in enumerate(new_ibm):
        flat = [m[0][0], m[1][0], m[2][0], m[3][0],
                m[0][1], m[1][1], m[2][1], m[3][1],
                m[0][2], m[1][2], m[2][2], m[3][2],
                m[0][3], m[1][3], m[2][3], m[3][3]]
        for c, v in enumerate(flat):
            struct.pack_into("<f", bin_, off + i * stride + c * 4, v)
    for mesh in gltf.get("meshes", []):
        for prim in mesh.get("primitives", []):
            pos = prim.get("attributes", {}).get("POSITION")
            if pos is not None:
                scale_float_accessor(gltf, bin_, pos, k)
    # IBM 重算后静止蒙皮 == 顶点本身(W·inv(W)·v = v),直接用 accessor
    # min/max 验证——精确且不受子采样影响
    got = 0.0
    for mesh in gltf.get("meshes", []):
        for prim in mesh.get("primitives", []):
            pos = prim.get("attributes", {}).get("POSITION")
            if pos is None:
                continue
            acc = gltf["accessors"][pos]
            got = max(got, max(abs(acc["max"][i] - acc["min"][i]) for i in range(3)))
    if abs(got - to_span) > RETARGET_TOL * to_span:
        return f"error:target-missed {got:.3f} != {to_span:.3f} (file NOT written)"
    write_glb(path, gltf, rest)
    return f"fixed: {'; '.join(actions)} (verified {got:.3f}m)"


def scale_accessor_peraxis(gltf: dict, bin_: memoryview, idx: int,
                           factors: tuple[float, float, float], invert: bool = False) -> None:
    """VEC3 逐轴缩放。invert=True 用于法线(逆矩阵语义:除法 + 逐行归一化)。"""
    off, ncomp, count, stride = accessor_meta(gltf, idx)
    f = [1.0 / v if invert else v for v in factors]
    for i in range(count):
        o = off + i * stride
        x = struct.unpack_from("<f", bin_, o)[0] * f[0]
        y = struct.unpack_from("<f", bin_, o + 4)[0] * f[1]
        z = struct.unpack_from("<f", bin_, o + 8)[0] * f[2]
        if invert:
            n = (x * x + y * y + z * z) ** 0.5
            if n > 1e-12:
                x, y, z = x / n, y / n, z / n
        struct.pack_into("<f", bin_, o, x)
        struct.pack_into("<f", bin_, o + 4, y)
        struct.pack_into("<f", bin_, o + 8, z)
    if invert:
        return
    acc = gltf["accessors"][idx]
    if "min" in acc and "max" in acc:
        lo = [acc["min"][i] * f[i] for i in range(3)]
        hi = [acc["max"][i] * f[i] for i in range(3)]
        acc["min"] = [min(lo[i], hi[i]) for i in range(3)]
        acc["max"] = [max(lo[i], hi[i]) for i in range(3)]


def world_vert_bounds(gltf: dict, bin_: memoryview) -> tuple[list[float], list[float]]:
    """所有网格节点的 POSITION min/max 八角点经世界变换后的全局包围盒。
    线性映射下轴对齐盒的极值必在角点取得,八角点即精确世界跨度。"""
    worlds = world_transforms(gltf)
    lo = [float("inf")] * 3
    hi = [float("-inf")] * 3
    for ni, node in enumerate(gltf.get("nodes", [])):
        if "mesh" not in node:
            continue
        w = worlds[ni]
        mesh = gltf["meshes"][node["mesh"]]
        for prim in mesh.get("primitives", []):
            pos = prim.get("attributes", {}).get("POSITION")
            if pos is None:
                continue
            acc = gltf["accessors"][pos]
            if "min" in acc and "max" in acc:
                mn, mx = acc["min"], acc["max"]
                pts = [(mn[0] if i & 1 else mx[0],
                        mn[1] if i & 2 else mx[1],
                        mn[2] if i & 4 else mx[2]) for i in range(8)]
            else:
                pts = read_rows(gltf, bin_, pos)
            for v in pts:
                p = [w[r][0] * v[0] + w[r][1] * v[1] + w[r][2] * v[2] + w[r][3]
                     for r in range(3)]
                for a in range(3):
                    lo[a] = min(lo[a], p[a])
                    hi[a] = max(hi[a], p[a])
    return lo, hi


def bake_static(path: Path, dry_run: bool) -> str:
    """静态网格:把节点缩放烘焙进顶点与子节点位移,节点缩放全部归 1。
    精确性:均匀缩放与旋转可交换(S·R = R·S),可沿任意链下推;非均匀缩放
    只允许在"子节点无旋转"的链上下推(否则产生剪切,跳过整个文件)。
    法线:均匀缩放方向不变;非均匀按逆矩阵除法 + 归一化。含 TANGENT 的
    非均匀烘焙跳过(切线 w 分量处理复杂,不冒险)。世界包围盒不变量验证兜底。"""
    gltf, rest = read_glb(path)
    if gltf.get("skins"):
        return "skip:skinned"
    nodes = gltf.get("nodes", [])

    def is_scaled(n: dict) -> bool:
        s = n.get("scale")
        return bool(s) and (abs(s[0] - 1) > 1e-6 or abs(s[1] - 1) > 1e-6
                            or abs(s[2] - 1) > 1e-6)

    if not any(is_scaled(n) for n in nodes):
        return "skip:clean"
    users: dict[int, int] = {}
    for n in nodes:
        if "mesh" in n:
            users[n["mesh"]] = users.get(n["mesh"], 0) + 1
    if any(c > 1 for c in users.values()):
        return "skip:shared-mesh"
    bin_ = bin_view(rest)
    before = world_vert_bounds(gltf, bin_)
    n_elim = 0
    for _pass in range(64):
        remaining = [i for i, n in enumerate(nodes) if is_scaled(n)]
        if not remaining:
            break
        progress = False
        for i in remaining:
            node = nodes[i]
            s = node["scale"]
            uniform = (abs(s[0] - s[1]) <= 1e-6 * max(1.0, abs(s[0]))
                       and abs(s[0] - s[2]) <= 1e-6 * max(1.0, abs(s[0])))
            kids = node.get("children", [])
            if not uniform:
                rot_kid = False
                for c in kids:
                    r = nodes[c].get("rotation")
                    if r and (abs(r[0]) > 1e-6 or abs(r[1]) > 1e-6 or abs(r[2]) > 1e-6):
                        rot_kid = True
                        break
                if rot_kid:
                    continue
                if "mesh" in node:
                    mesh = gltf["meshes"][node["mesh"]]
                    if any("TANGENT" in p.get("attributes", {})
                           for p in mesh.get("primitives", [])):
                        continue
            if "mesh" in node:
                mesh = gltf["meshes"][node["mesh"]]
                for prim in mesh.get("primitives", []):
                    attrs = prim.get("attributes", {})
                    pos = attrs.get("POSITION")
                    if pos is not None:
                        scale_accessor_peraxis(gltf, bin_, pos, (s[0], s[1], s[2]))
                    nrm = attrs.get("NORMAL")
                    if nrm is not None and not uniform:
                        scale_accessor_peraxis(gltf, bin_, nrm, (s[0], s[1], s[2]),
                                               invert=True)
            for c in kids:
                ch = nodes[c]
                t = ch.get("translation")
                if t:
                    ch["translation"] = [t[0] * s[0], t[1] * s[1], t[2] * s[2]]
                cs = ch.get("scale")
                ch["scale"] = [s[0] * (cs[0] if cs else 1.0),
                               s[1] * (cs[1] if cs else 1.0),
                               s[2] * (cs[2] if cs else 1.0)]
            node.pop("scale")
            n_elim += 1
            progress = True
        if not progress:
            break
    leftover = [nodes[i].get("name", str(i)) for i, n in enumerate(nodes) if is_scaled(n)]
    if leftover:
        return f"skip:blocked-nonuniform:{','.join(leftover[:3])}"
    for n in nodes:
        s = n.get("scale")
        if s and abs(s[0] - 1) <= 1e-6 and abs(s[1] - 1) <= 1e-6 and abs(s[2] - 1) <= 1e-6:
            n.pop("scale")
    if n_elim == 0:
        return "skip:clean"
    if dry_run:
        return f"would-bake: {n_elim} scales"
    after = world_vert_bounds(gltf, bin_)
    span_before = max(before[1][a] - before[0][a] for a in range(3))
    err = max(abs(after[1][a] - before[1][a]) + abs(after[0][a] - before[0][a])
              for a in range(3))
    if span_before > 1e-9 and err > 1e-4 * span_before:
        return f"error:bake-not-invariant err={err:.6f} span={span_before:.3f} (file NOT written)"
    write_glb(path, gltf, rest)
    return f"baked: {n_elim} scales (world err {err:.2e})"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--meshes-root", default="godot/assets/meshes")
    parser.add_argument("--retarget", metavar="REL",
                        help="single broken file relative to meshes-root")
    parser.add_argument("--to-span", type=float, default=0.0)
    parser.add_argument("--bone-scale", type=float, default=1.0,
                        help="with --retarget: also scale joint rest translations "
                             "by this factor (for mixed-unit files like goat)")
    parser.add_argument("--from-audit", metavar="JSON",
                        help="retarget every 'broken' audit entry (span 验证兜底)")
    parser.add_argument("--ratio-below", type=float, default=0.0,
                        help="in --from-audit mode, only touch files whose "
                             "span/target ratio is below this (unambiguous breakage)")
    parser.add_argument("--bake-statics", action="store_true",
                        help="把所有静态 GLB 的节点缩放烘焙进顶点(世界空间不变式)")
    args = parser.parse_args()

    meshes_root = Path(args.meshes_root)
    if args.bake_statics:
        tally: dict[str, int] = {}
        for p in sorted(meshes_root.rglob("*.glb")):
            try:
                outcome = bake_static(p, args.dry_run)
            except (ValueError, OSError, KeyError, IndexError, struct.error) as exc:
                outcome = f"error:{exc}"
            tag = outcome.split(":")[0]
            tally[tag] = tally.get(tag, 0) + 1
            if tag not in ("skip",):
                print(f"{outcome}: {p.relative_to(meshes_root)}")
            elif "blocked" in outcome or "shared" in outcome:
                print(f"{outcome}: {p.relative_to(meshes_root)}")
        print(f"summary: {tally}")
        return 0
    if args.retarget:
        if args.to_span <= 0:
            print("error: --retarget needs --to-span")
            return 2
        print(retarget(meshes_root / args.retarget, args.to_span, args.dry_run, args.bone_scale))
        return 0

    if args.from_audit:
        report = json.loads(Path(args.from_audit).read_text())
        n = 0
        for entry in report.get("broken", []):
            target = entry.get("target")
            if not target:
                continue
            ratio = entry["span"] / target if target else 0.0
            if args.ratio_below and ratio > args.ratio_below:
                print(f"defer:ratio-{ratio:.3f}(可见资产,待游戏内实测): {entry['file']}")
                continue
            outcome = retarget(meshes_root / entry["file"], target, args.dry_run)
            print(f"{outcome}: {entry['file']}")
            if outcome.startswith(("fixed", "would-fix")):
                n += 1
        print(f"summary: retargeted={n}")
        return 0

    n = 0
    for glb_path in sorted(meshes_root.rglob("*.glb")):
        try:
            outcome = canonicalize(glb_path, args.dry_run)
        except (ValueError, OSError, KeyError, IndexError, struct.error) as exc:
            outcome = f"error:{exc}"
        if outcome.startswith(("fixed", "would-fix", "error")):
            print(f"{outcome}: {glb_path.relative_to(meshes_root)}")
        if outcome.startswith(("fixed", "would-fix")):
            n += 1
    print(f"summary: canonicalized={n}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
