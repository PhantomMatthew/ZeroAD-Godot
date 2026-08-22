"""Skinned fauna 动画 GLB 的英寸位移轨道修正(转化器侧沉淀).

C++ 的 Collada 转换器忽略 DAE `<unit meter="X"/>`,裸坐标即游戏米;Blender 导出
时部分骨骼的动画位移轨道仍停留在英寸。运行时曾用白名单补偿(2026-08-22),现
固化到管线。

规则:

A. 位移轨道模长 > 8m 且目标骨名含 "antler" → × 0.0254。deer_* 十个剪辑的
   antler 轨道仍是英寸(约 92),而 deer_mesh 骨架已是米制(prop-antler rest
   仅 0.47m);原样写入会把挂在该关节的蒙皮拉到约 85m(瞪羚/鹿整体看起来
   约 19 倍)。修复后约 2.3m,低于 8m 门槛,幂等。

B. 其余 > 8m 的位移轨道只警告不动手。当前语料里这类轨道几乎都是 IK 辅助骨
   (handIK/elbow/knee)、抛射体、船桨、城门枢轴——不蒙皮或本来就属于大体型
   结构;goat_* 是全骨架单位错乱,需单独排查。

注意:不要对本仓库的标记网格(waypoint_flag / garrison_flag / target_marker)
做顶点 × 1/unit。它们的逆绑定矩阵已带 ×39.37/×100 缩放,静止蒙皮位姿就是
C++ 尺寸(旗 8.5m、驻军旗 6m);再乘顶点会双重应用(实测会变成 336m)。
2026-08-22 的运行时白名单补偿即因此撤销,GLB 从快照回滚。

用法: python3 fix_glb_skeleton_unit_space.py [--dry-run] [--anims-root PATH]
"""
from __future__ import annotations

import argparse
import json
import struct
import sys
from pathlib import Path

INCH_TO_METER = 0.0254
INCH_KEY_MIN_METERS = 8.0

_NCOMP = {"SCALAR": 1, "VEC3": 3, "VEC4": 4, "MAT4": 16}


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


def accessor_view(gltf: dict, bin_: memoryview, idx: int) -> tuple[memoryview, int, int, int]:
    """(view, ncomp, count, stride) for a float32 accessor, honoring byteStride."""
    acc = gltf["accessors"][idx]
    bv = gltf["bufferViews"][acc["bufferView"]]
    off = bv.get("byteOffset", 0) + acc.get("byteOffset", 0)
    ncomp = _NCOMP[acc["type"]]
    stride = bv.get("byteStride") or ncomp * 4
    return bin_[off:], ncomp, acc["count"], stride


def scale_accessor(gltf: dict, bin_: memoryview, idx: int, factor: float) -> None:
    view, ncomp, count, stride = accessor_view(gltf, bin_, idx)
    for i in range(count):
        for c in range(ncomp):
            o = i * stride + c * 4
            struct.pack_into("<f", view, o, struct.unpack_from("<f", view, o)[0] * factor)
    acc = gltf["accessors"][idx]
    for key in ("min", "max"):
        if key in acc:
            acc[key] = [v * factor for v in acc[key]]


def track_max_key(gltf: dict, bin_: memoryview, sampler: dict) -> float:
    view, ncomp, count, stride = accessor_view(gltf, bin_, sampler["output"])
    assert ncomp == 3
    longest = 0.0
    for i in range(count):
        x, y, z = struct.unpack_from("<3f", view, i * stride)
        longest = max(longest, (x * x + y * y + z * z) ** 0.5)
    return longest


def process_anim(path: Path, dry_run: bool) -> tuple[list[str], list[str]]:
    """(actions, warnings) — antler inch tracks; everything else big is warn-only."""
    gltf, rest_raw = read_glb(path)
    rest = bytearray(rest_raw)
    bin_ = memoryview(rest)[8:]
    actions: list[str] = []
    warnings: list[str] = []

    changed = False
    for anim in gltf.get("animations", []):
        for ch in anim.get("channels", []):
            if ch.get("target", {}).get("path") != "translation":
                continue
            name = gltf["nodes"][ch["target"]["node"]].get("name", "")
            sampler = anim["samplers"][ch["sampler"]]
            mag = track_max_key(gltf, bin_, sampler)
            if mag <= INCH_KEY_MIN_METERS:
                continue
            if "antler" in name.lower():
                actions.append(f"antler-inch:{name} {mag:.2f}->{mag * INCH_TO_METER:.2f}")
                if not dry_run:
                    scale_accessor(gltf, bin_, sampler["output"], INCH_TO_METER)
                    changed = True
            else:
                warnings.append(f"big-translation:{name} {mag:.1f}m (unhandled, needs review)")

    if changed:
        write_glb(path, gltf, bytes(rest))
    return actions, warnings


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--anims-root", default="godot/assets/animations")
    args = parser.parse_args()

    n_anim = 0
    for glb_path in sorted(Path(args.anims_root).rglob("*.glb")):
        try:
            actions, warnings = process_anim(glb_path, args.dry_run)
        except (ValueError, OSError, KeyError, IndexError, struct.error) as exc:
            actions, warnings = [f"error:{exc}"], []
        for w in warnings:
            print(f"warn: {glb_path} [{w}]")
        if actions:
            tag = "would-fix" if args.dry_run else "fixed"
            print(f"{tag}: {glb_path} [{'; '.join(actions)}]")
            n_anim += 1

    print(f"summary: anims-touched={n_anim}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
