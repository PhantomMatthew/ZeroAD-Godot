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
   结构。

C. goat_* 四个剪辑:全骨架单位错乱(DAE 顶点米/骨骼厘米,Blender 烘焙时又把
   根骨 Base 的位移轨道除以了根节点缩放 2.27e-05)。网格侧已由
   normalize_glb_unit_space.py --bone-scale 0.01 修复(骨骼落到米制)。
   这里把动画轨道对齐:Base 轨道 × 该文件自己的根缩放值(烘焙除法的逆运算,
   直接回到米),其余轨道 × 0.01(厘米→米),Base 缩放轨道 440.47× 归一为 1
   (它原先与根节点 2.27e-05、厘米骨骼共同抵消),并剥掉动画文件里的根缩放。
   幂等:位移轨道 < 8m 且 Base 缩放≈1 时不触发。

注意:不要对本仓库的标记网格(waypoint_flag / garrison_flag / target_marker)
做顶点 × 1/unit。它们的逆绑定矩阵已带 ×39.37/×100 缩放,静止蒙皮位姿就是
C++ 尺寸(旗 8.5m、驻军旗 6m);再乘顶点会双重应用(实测会变成 336m)。
2026-08-22 的运行时白名单补偿即因此撤销,GLB 从快照回滚。

用法: python3 fix_glb_skeleton_unit_space.py [--dry-run] [--anims-root PATH]
"""
from __future__ import annotations

import argparse
import bisect
import json
import struct
import sys
import xml.parsers.expat
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
    count = acc["count"]
    return bin_[off : off + count * stride], ncomp, count, stride


def scale_accessor(gltf: dict, bin_: memoryview, idx: int, factor: float) -> None:
    view, ncomp, count, stride = accessor_view(gltf, bin_, idx)
    for i in range(count):
        for c in range(ncomp):
            o = i * stride + c * 4
            struct.pack_into("<f", view, o, struct.unpack_from("<f", view, o)[0] * factor)
    acc = gltf["accessors"][idx]
    if "min" in acc and "max" in acc:
        for key in ("min", "max"):
            acc[key] = [v * factor for v in acc[key]]


def set_vec3_accessor_identity(gltf: dict, bin_: memoryview, idx: int) -> None:
    view, ncomp, count, stride = accessor_view(gltf, bin_, idx)
    if ncomp != 3:
        raise ValueError(f"expected VEC3 scale accessor, got {ncomp} components")
    for i in range(count):
        struct.pack_into("<3f", view, i * stride, 1.0, 1.0, 1.0)
    acc = gltf["accessors"][idx]
    if "min" in acc and "max" in acc:
        for key in ("min", "max"):
            acc[key] = [1.0, 1.0, 1.0]


def track_max_key(gltf: dict, bin_: memoryview, sampler: dict) -> float:
    view, ncomp, count, stride = accessor_view(gltf, bin_, sampler["output"])
    if ncomp != 3:
        raise ValueError(f"expected VEC3 animation accessor, got {ncomp} components")
    longest = 0.0
    for i in range(count):
        x, y, z = struct.unpack_from("<3f", view, i * stride)
        longest = max(longest, (x * x + y * y + z * z) ** 0.5)
    return longest


class _DaeAnimationHandler:
    """Minimal, XXE-disabled reader for COLLADA animation arrays and links."""

    def __init__(self) -> None:
        self.sources: dict[str, list[float]] = {}
        self.samplers: dict[str, dict[str, str]] = {}
        self.channels: list[tuple[str, str]] = []
        self._source = ""
        self._sampler = ""
        self._float_text: list[str] | None = None

    def start_element(self, name: str, attrs: dict[str, str]) -> None:
        local = name.rsplit(":", 1)[-1]
        if local == "source":
            self._source = attrs.get("id", "")
        elif local == "sampler":
            self._sampler = attrs.get("id", "")
            if self._sampler:
                self.samplers[self._sampler] = {}
        elif local == "input" and self._sampler:
            self.samplers[self._sampler][attrs.get("semantic", "")] = attrs.get(
                "source", ""
            ).lstrip("#")
        elif local == "channel":
            self.channels.append(
                (attrs.get("source", "").lstrip("#"), attrs.get("target", ""))
            )
        elif local == "float_array" and self._source:
            self._float_text = []

    def characters(self, content: str) -> None:
        if self._float_text is not None:
            self._float_text.append(content)

    def end_element(self, name: str) -> None:
        local = name.rsplit(":", 1)[-1]
        if local == "float_array" and self._float_text is not None:
            self.sources[self._source] = [
                float(value) for value in "".join(self._float_text).split()
            ]
            self._float_text = None
        elif local == "source":
            self._source = ""
        elif local == "sampler":
            self._sampler = ""


def _read_dae_animation_links(
    path: Path,
) -> tuple[dict[str, list[float]], dict[str, dict[str, str]], list[tuple[str, str]]]:
    handler = _DaeAnimationHandler()
    parser = xml.parsers.expat.ParserCreate()
    parser.StartElementHandler = handler.start_element
    parser.EndElementHandler = handler.end_element
    parser.CharacterDataHandler = handler.characters
    parser.ExternalEntityRefHandler = lambda *_args: 0

    def reject_entity(*_args: object) -> None:
        raise ValueError(f"entity declarations are not allowed in {path}")

    parser.EntityDeclHandler = reject_entity
    with path.open("rb") as source:
        while chunk := source.read(1024 * 1024):
            parser.Parse(chunk, False)
        parser.Parse(b"", True)
    return handler.sources, handler.samplers, handler.channels


def repair_goat_translations_from_dae(
    path: Path, dae_path: Path, dry_run: bool
) -> list[str]:
    """Replace goat child-bone positions with the source DAE matrix translations.

    Blender's centimeter import decomposes animated goat matrices into position
    tracks that drift several metres from the bone rest (for example walk's
    shoulder_front_r becomes 5.4 m instead of the DAE's 2.5 m). Rotations remain
    valid, but those positions stretch weighted vertices after animation changes.
    The source matrices store child-local translations in centimeters; C++ treats
    the mesh's mixed-unit skeleton as centimeters, so convert these values to the
    normalized metre skeleton with x0.01. Base is intentionally retained from
    Blender because the armature/object transforms are folded into that root track.
    """
    gltf, rest_raw = read_glb(path)
    rest = bytearray(rest_raw)
    bin_ = memoryview(rest)[8:]
    sources, samplers, channels = _read_dae_animation_links(dae_path)
    dae_tracks: dict[str, tuple[list[float], list[tuple[float, float, float]]]] = {}
    for sampler_id, target in channels:
        if not target.endswith("/transform"):
            continue
        bone = target.split("/", 1)[0]
        if bone == "Base":
            continue
        sampler = samplers.get(sampler_id)
        if not sampler:
            continue
        times = sources.get(sampler.get("INPUT", ""))
        matrices = sources.get(sampler.get("OUTPUT", ""))
        if times is None or matrices is None:
            continue
        if not times or len(matrices) != len(times) * 16:
            continue
        translations = [
            (matrices[i + 3] * 0.01, matrices[i + 7] * 0.01, matrices[i + 11] * 0.01)
            for i in range(0, len(matrices), 16)
        ]
        dae_tracks[bone] = (times, translations)

    def sample(
        times: list[float], values: list[tuple[float, float, float]], time: float
    ) -> tuple[float, float, float]:
        if time <= times[0]:
            return values[0]
        if time >= times[-1]:
            return values[-1]
        index = bisect.bisect_right(times, time) - 1
        duration = times[index + 1] - times[index]
        factor = (time - times[index]) / duration if duration > 0.0 else 0.0
        return tuple(
            values[index][axis] * (1.0 - factor) + values[index + 1][axis] * factor
            for axis in range(3)
        )

    actions: list[str] = []
    changed = False
    bones_matched = 0
    for animation in gltf.get("animations", []):
        for channel in animation.get("channels", []):
            target = channel.get("target", {})
            if target.get("path") != "translation":
                continue
            bone = gltf["nodes"][target["node"]].get("name", "")
            track = dae_tracks.get(bone)
            if track is None:
                continue
            bones_matched += 1
            sampler_info = animation["samplers"][channel["sampler"]]
            time_view, time_components, count, time_stride = accessor_view(
                gltf, bin_, sampler_info["input"]
            )
            output_view, output_components, output_count, output_stride = accessor_view(
                gltf, bin_, sampler_info["output"]
            )
            if time_components != 1 or output_components != 3 or output_count != count:
                raise ValueError(f"unexpected goat track layout for {bone}")
            repaired = [
                sample(
                    track[0],
                    track[1],
                    struct.unpack_from("<f", time_view, i * time_stride)[0],
                )
                for i in range(count)
            ]
            actions.append(f"dae-position:{bone} keys={count}")
            if dry_run:
                continue
            for i, value in enumerate(repaired):
                struct.pack_into("<3f", output_view, i * output_stride, *value)
            accessor = gltf["accessors"][sampler_info["output"]]
            if "min" in accessor and "max" in accessor:
                accessor["min"] = [min(value[axis] for value in repaired) for axis in range(3)]
                accessor["max"] = [max(value[axis] for value in repaired) for axis in range(3)]
            changed = True
    if dae_tracks and bones_matched == 0:
        raise ValueError(
            f"{path.name}: no GLB bone matched a DAE animation target in {dae_path.name} "
            "— bone naming likely diverged (e.g. armature-prefixed export); refusing to "
            "silently no-op"
        )
    if changed:
        write_glb(path, gltf, bytes(rest))
    return actions


def process_goat_anim(
    path: Path, dry_run: bool, dae_path: Path | None = None
) -> tuple[list[str], list[str]]:
    """规则 C:goat_* 剪辑。Base 轨道 × 文件自身的根缩放(烘焙除法的逆运算),
    其余位移轨道 × 0.01,Base 缩放轨道归一为 1,并剥掉残留的根节点缩放。
    位移与缩放都已规范时不触发(幂等)。"""
    gltf, rest_raw = read_glb(path)
    rest = bytearray(rest_raw)
    bin_ = memoryview(rest)[8:]
    actions: list[str] = []

    has_broken_translation = False
    broken_base_scale: list[dict] = []
    for anim in gltf.get("animations", []):
        for ch in anim.get("channels", []):
            target = ch.get("target", {})
            name = gltf["nodes"][target["node"]].get("name", "")
            sampler = anim["samplers"][ch["sampler"]]
            if target.get("path") == "translation":
                if track_max_key(gltf, bin_, sampler) > INCH_KEY_MIN_METERS:
                    has_broken_translation = True
            elif target.get("path") == "scale" and name == "Base":
                # Identity magnitude is sqrt(3); the known conversion artifact is
                # sqrt(3) * 440.474. Keep a wide gap from plausible authored scale.
                if track_max_key(gltf, bin_, sampler) > 100.0:
                    broken_base_scale.append(sampler)
    if not has_broken_translation and not broken_base_scale:
        dae_actions = (
            repair_goat_translations_from_dae(path, dae_path, dry_run)
            if dae_path is not None and dae_path.exists()
            else []
        )
        return dae_actions, []

    root_scale = None
    for node in gltf.get("nodes", []):
        s = node.get("scale")
        if s is not None and abs(s[0]) < 1e-3:
            if root_scale is not None:
                raise ValueError(
                    f"{path.name}: multiple tiny-scale nodes found; expected exactly one "
                    "root-scale artifact — needs manual review"
                )
            root_scale = s[0]
            if not dry_run:
                node.pop("scale")
            actions.append(f"strip-root:{node.get('name')}={s[0]:.6g}")
    if has_broken_translation and root_scale is None:
        return [], ["goat anim has broken tracks but no tiny root scale — needs review"]

    if has_broken_translation:
        assert root_scale is not None
        for anim in gltf.get("animations", []):
            for ch in anim.get("channels", []):
                if ch.get("target", {}).get("path") != "translation":
                    continue
                name = gltf["nodes"][ch["target"]["node"]].get("name", "")
                sampler = anim["samplers"][ch["sampler"]]
                factor = root_scale if name == "Base" else 0.01
                mag = track_max_key(gltf, bin_, sampler)
                actions.append(f"goat:{name} {mag:.2f}->{mag * factor:.3f}")
                if not dry_run:
                    scale_accessor(gltf, bin_, sampler["output"], factor)

    for sampler in broken_base_scale:
        mag = track_max_key(gltf, bin_, sampler)
        actions.append(f"goat-scale:Base {mag:.2f}->1")
        if not dry_run:
            set_vec3_accessor_identity(gltf, bin_, sampler["output"])

    if not dry_run:
        write_glb(path, gltf, bytes(rest))
    if dae_path is not None and dae_path.exists():
        actions.extend(repair_goat_translations_from_dae(path, dae_path, dry_run))
    return actions, []


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
    parser.add_argument(
        "--dae-root",
        default="../binaries/data/mods/public/art/animation",
        help=(
            "source DAE animation root used to repair goat local translations "
            "(default assumes cwd is godot/, matching run_full_pipeline.sh)"
        ),
    )
    args = parser.parse_args()

    n_anim = 0
    anims_root = Path(args.anims_root)
    dae_root = Path(args.dae_root)
    for glb_path in sorted(anims_root.rglob("*.glb")):
        try:
            if glb_path.stem.startswith("goat_"):
                dae_path = dae_root / glb_path.relative_to(anims_root).with_suffix(".dae")
                actions, warnings = process_goat_anim(glb_path, args.dry_run, dae_path)
            else:
                actions, warnings = process_anim(glb_path, args.dry_run)
        except (
            ValueError,
            OSError,
            KeyError,
            IndexError,
            struct.error,
            xml.parsers.expat.ExpatError,
        ) as exc:
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
