"""Stage verbatim-read art/textures into godot/assets/upstream (self-hosted mirror).

The game reads several texture categories directly from the upstream 0 A.D.
checkout at runtime (skyboxes, animated water normals, UI portraits/buttons,
cursors, selection outlines, particles, terrain alpha shape maps, terrain
type XMLs). The source tree is located via the ZEROAD_UPSTREAM environment
variable pointing at the upstream 0 A.D. checkout root (repo-root binaries/
junctions were removed on 2026-09-12); without the staged mirror, layouts
other than a full dev checkout lose these assets wholesale.

This tool mirrors those categories, preserving the upstream relative path
under godot/assets/upstream/, converting .dds/.tga to .png (Godot cannot
decode DDS at runtime and TGA support is loader-dependent). RuntimePaths
probes this mirror as a fallback, so consumers need no per-file changes.

Run: blender --background --python stage_upstream_textures.py
(see run_full_pipeline.sh for $BLENDER resolution; idempotent — existing
destination files are skipped).
"""

import os
import shutil
from pathlib import Path

import bpy

_TOOLS_DIR = os.path.dirname(os.path.abspath(__file__))
_GODOT_DIR = os.path.dirname(_TOOLS_DIR)          # godot/
_REPO_ROOT = Path(_GODOT_DIR).parent.resolve()    # repo root
_DST_ROOT = (Path(_GODOT_DIR) / "assets" / "upstream" / "art").resolve()

# 上游源:ZEROAD_UPSTREAM 指向上游 0 A.D. 检出根(仓库根 binaries/ 软链已于
# 2026-09-12 禁用);未设置即拒绝运行。
_upstream = os.environ.get("ZEROAD_UPSTREAM")
if not _upstream:
    raise SystemExit(
        "ZEROAD_UPSTREAM not set: point it at the upstream 0 A.D. checkout root"
    )
_SRC_ROOT = (Path(_upstream) / "binaries" / "data" / "mods" / "public" / "art").resolve()

# 目标必须落在仓库内(路径穿越护栏:resolve 后 relative_to 校验);源侧由
# relative_to(_SRC_ROOT) 兜底,rglob 结果越出源根即抛错拒绝。
_ALLOWED_DST_ROOT = _REPO_ROOT

# Runtime-direct-read categories (grep RuntimePaths.FindPublicPath consumers).
# Terrain *color* textures are NOT here — they live converted under
# assets/textures/terrain via convert_dds_textures.py; only the alpha shape
# maps (SplatBaker) and terrain type XMLs are direct reads.
TREES = [
    "textures/animated",
    "textures/cursors",
    "textures/misc",
    "textures/particles",
    "textures/selection",
    "textures/skies",
    "textures/ui",
    "textures/terrain/alphamaps",
    "terrains",
]
_VERBATIM_EXT = {".png", ".txt", ".xml", ".jpg"}
_CONVERT_EXT = {".dds", ".tga"}


def _confined_src(path: Path) -> Path:
    """Resolve and require the path to stay inside the upstream source root."""
    resolved = path.resolve()
    resolved.relative_to(_SRC_ROOT)  # raises ValueError when outside the root
    return resolved


def _confined_dst(path: Path) -> Path:
    """Resolve and require the path to stay inside the repo (destination guard)."""
    resolved = path.resolve()
    if not resolved.is_relative_to(_ALLOWED_DST_ROOT):
        raise ValueError(f"path escapes allowed root: {resolved}")
    return resolved


def convert_to_png(src: Path, dst: Path) -> str:
    """Decode src via Blender and save as PNG at dst; return error or ''."""
    try:
        img = bpy.data.images.load(str(src))
        _ = img.pixels[0]
        os.makedirs(dst.parent, exist_ok=True)
        img.filepath_raw = str(dst)
        img.file_format = "PNG"
        img.save()
        bpy.data.images.remove(img)
        return ""
    except Exception as exc:  # noqa: BLE001 - report and continue batch
        return str(exc)


copied, converted, skipped = 0, 0, 0
failed: list[str] = []
for tree in TREES:
    try:
        src_root = _confined_src(_SRC_ROOT / tree)
    except ValueError as exc:
        failed.append(str(exc))
        continue
    if not src_root.is_dir():
        failed.append(f"MISSING TREE {tree}")
        continue
    for src in sorted(src_root.rglob("*")):
        if not src.is_file():
            continue
        ext = src.suffix.lower()
        if ext not in _VERBATIM_EXT and ext not in _CONVERT_EXT:
            continue
        try:
            rel = _confined_src(src).relative_to(_SRC_ROOT)
            dst = _confined_dst(_DST_ROOT / rel)
        except ValueError as exc:
            failed.append(str(exc))
            continue
        if ext in _CONVERT_EXT:
            dst = dst.with_suffix(".png")
        if dst.exists():
            skipped += 1
            continue
        if ext in _CONVERT_EXT:
            err = convert_to_png(src, dst)
            if err:
                failed.append(f"{rel}: {err}")
            else:
                converted += 1
        else:
            os.makedirs(dst.parent, exist_ok=True)
            shutil.copyfile(src, dst)
            copied += 1

print(f"STAGED copied={copied} converted={converted} skipped={skipped} "
      f"failed={len(failed)}")
for entry in failed[:20]:
    print("FAILED", entry)
