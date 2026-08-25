#!/usr/bin/env python3
"""Copy tooltip UI icons from the upstream 0 A.D. tree into res://assets.

godot/assets/ is a gitignored build product, so every icon the GUI needs at
runtime must be re-copyable from `binaries/` by a committed tool. This covers
the RichTextLabel [img] icons used by GameTooltip:

  resources dir  (art/textures/ui/session/icons/resources/):
    food/wood/stone/metal _small.png   — cost / dropsite / loot / gather rows
    population_small.png              — Cost population (setup_resources.xml)
    time_small.png                    — Cost time / tech research time
    fruit/grain/meat/rice/fish _small.png — gather-rate subtypes (food_*.xml)
  icons dir  (art/textures/ui/session/icons/):
    promote.png                       — xp loot icon (icon_xp)

Idempotent: skips files that already exist with identical size.
Run from anywhere; resolves ../binaries relative to this file's repo root.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

GODOT_DIR = Path(__file__).resolve().parent.parent

# (source path relative to binaries/data/mods/public/art/textures/ui/session,
#  destination path relative to godot/assets)
ICONS: list[tuple[str, str]] = [
    ("icons/resources/food_small.png", "ui/resources/food_small.png"),
    ("icons/resources/wood_small.png", "ui/resources/wood_small.png"),
    ("icons/resources/stone_small.png", "ui/resources/stone_small.png"),
    ("icons/resources/metal_small.png", "ui/resources/metal_small.png"),
    ("icons/resources/population_small.png", "ui/resources/population_small.png"),
    ("icons/resources/time_small.png", "ui/resources/time_small.png"),
    ("icons/resources/fruit_small.png", "ui/resources/fruit_small.png"),
    ("icons/resources/grain_small.png", "ui/resources/grain_small.png"),
    ("icons/resources/meat_small.png", "ui/resources/meat_small.png"),
    ("icons/resources/rice_small.png", "ui/resources/rice_small.png"),
    ("icons/resources/fish_small.png", "ui/resources/fish_small.png"),
    ("icons/promote.png", "ui/resources/xp.png"),
]


def find_binaries_dir(explicit: str | None) -> Path | None:
    """Upstream resolution order: explicit arg > ZEROAD_UPSTREAM > walk up."""
    if explicit:
        return Path(explicit)
    env = Path(__import__("os").environ.get("ZEROAD_UPSTREAM", ""))
    candidates = [env] if str(env) else []
    for parent in GODOT_DIR.parents:
        candidates.append(parent / "binaries")
    for cand in candidates:
        modroot = cand / "data" / "mods" / "public" / "art"
        if modroot.is_dir():
            return cand
    return None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("upstream", nargs="?", help="path to upstream 0ad root (contains binaries/)")
    args = parser.parse_args()

    binaries = find_binaries_dir(args.upstream)
    if binaries is None:
        print("error: could not locate binaries/ (pass the upstream path or set ZEROAD_UPSTREAM)")
        return 1
    art_root = binaries / "data" / "mods" / "public" / "art" / "textures" / "ui" / "session"
    dest_root = GODOT_DIR / "assets"

    copied = skipped = 0
    for src_rel, dst_rel in ICONS:
        src = art_root / src_rel
        dst = dest_root / dst_rel
        if not src.is_file():
            print(f"error: missing source {src}")
            return 1
        if dst.is_file() and dst.stat().st_size == src.stat().st_size:
            skipped += 1
            continue
        dst.parent.mkdir(parents=True, exist_ok=True)
        dst.write_bytes(src.read_bytes())
        print(f"copied {src_rel} -> assets/{dst_rel}")
        copied += 1
    print(f"done: {copied} copied, {skipped} up-to-date")
    return 0


if __name__ == "__main__":
    sys.exit(main())
