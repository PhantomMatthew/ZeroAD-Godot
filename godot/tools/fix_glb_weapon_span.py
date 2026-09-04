"""手持武器 GLB 的跨度上限修正(玩家观感校准)。

DAE 裸坐标×节点缩放的 C++ 语义会让部分武器(span>5m)视觉过长(玩家报告
"枪头和长枪巨大")。本脚本按 actor 路径(props/units/weapons/)枚举手持
武器网格,把 SPAN(顶点 min..max 全跨度,非单侧 max)压到:
  - 超长枪特型(sarissa/hele_sr_p/hele_sp_p/han_champion_spear): 6.5m
  - 其余枪/矛/杆: 4.5m(≈2.5×士兵身高,对齐 C++ 游戏观感)
均匀缩放(乘法,保留各向比例)。

用法: python3 fix_glb_weapon_span.py [--meshes-root PATH] [--actors-root PATH]
"""
from __future__ import annotations

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

import argparse, glob, json, os, re, struct

def main() -> int:
    # 常量化根目录(污点源=外部输入,连根拔;自定义走 run_full_pipeline.sh)。
    class _Args: pass
    args = _Args()
    args.meshes_root = str(_Path(_REPO_ROOT) / "godot" / "assets" / "meshes")
    args.actors_root = str(_Path(_REPO_ROOT) / "binaries" / "data" / "mods" / "public" / "art" / "actors")

    meshes = set()
    for p in glob.glob(args.actors_root + "/props/units/weapons/**/*.xml", recursive=True):
        for m in re.finditer(r"<mesh>([^<]+)</mesh>", open(p, errors="ignore").read()):
            meshes.add(m.group(1).strip().replace(".dae", ""))

    def target_for(rel: str) -> float:
        if any(k in rel for k in ("sarissa", "hele_sr_p", "hele_sp_p", "han_champion_spear")):
            return 6.5
        return 4.5

    fixed = 0
    for rel in sorted(meshes):
        gp = os.path.join(args.meshes_root, rel + ".glb")
        if not os.path.exists(gp):
            continue
        data = open(gp, "rb").read()
        if data[:4] != b"glTF":
            continue
        jl = struct.unpack("<I", data[12:16])[0]
        j = json.loads(data[20 : 20 + jl])
        tgt, changed = target_for(rel), False
        for n in j.get("nodes", []):
            if "mesh" not in n:
                continue
            acc = j["accessors"][j["meshes"][n["mesh"]]["primitives"][0]["attributes"]["POSITION"]]
            mn, mx = acc.get("min", [-1, -1, -1]), acc.get("max", [1, 1, 1])
            s = n.get("scale", [1, 1, 1])
            span = max(abs(mx[i] - mn[i]) for i in range(3)) * max(abs(v) for v in s)
            if span > tgt + 0.05:
                k = tgt / span
                n["scale"] = [round(v * k, 6) for v in s]
                changed = True
        if changed:
            gp = _require_within_repo(gp)
            payload = json.dumps(j, separators=(",", ":")).encode()
            payload += b" " * ((4 - len(payload) % 4) % 4)
            rest = data[20 + jl :]
            gp = _safe_repo_path(gp)
            _Path(gp).write_bytes(
                struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(payload) + len(rest))
                + struct.pack("<II", len(payload), 0x4E4F534A) + payload + rest)
            fixed += 1
    print(f"weapon span fixed: {fixed} GLB(s)")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
