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
import argparse, glob, json, os, re, struct

def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--meshes-root", default="godot/assets/meshes")
    ap.add_argument("--actors-root", default="binaries/data/mods/public/art/actors")
    args = ap.parse_args()

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
            payload = json.dumps(j, separators=(",", ":")).encode()
            payload += b" " * ((4 - len(payload) % 4) % 4)
            rest = data[20 + jl :]
            with open(gp, "wb") as f:
                f.write(struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(payload) + len(rest))
                        + struct.pack("<II", len(payload), 0x4E4F534A) + payload + rest)
            fixed += 1
    print(f"weapon span fixed: {fixed} GLB(s)")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
