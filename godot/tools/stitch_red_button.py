"""Stitch the 9-piece ModernButtonRed textures (mods/mod/.../global/modern/button/)
into a single 144x32 9-patch atlas (godot/assets/ui/modern/button/red-button-9patch.png).

The atlas is a derived artifact (no upstream single-image equivalent), committed
deliberately despite the godot/assets gitignore. Re-run after upstream art changes:
    python godot/tools/stitch_red_button.py
"""
from pathlib import Path

from PIL import Image

UPSTREAM = Path(__file__).resolve().parents[2] / "binaries/data/mods/mod/art/textures/ui/global/modern/button"
OUT = Path(__file__).resolve().parents[1] / "assets/ui/modern/button/red-button-9patch.png"


def load(name: str) -> Image.Image:
    return Image.open(UPSTREAM / name).convert("RGBA")


def main() -> None:
    lt, lc, lb = (load(f"red-unselected-left-{p}.png") for p in ("top", "center", "bottom"))
    ct, cc, cb = (load(f"red-unselected-center-{p}.png") for p in ("top", "center", "bottom"))
    rt, rc, rb = (load(f"red-unselected-right-{p}.png") for p in ("top", "center", "bottom"))
    out = Image.new("RGBA", (144, 32), (0, 0, 0, 0))
    out.paste(lt, (0, 0)); out.paste(ct, (8, 0)); out.paste(rt, (136, 0))
    out.paste(lc, (0, 8)); out.paste(cc, (8, 8)); out.paste(rc, (136, 8))
    out.paste(lb, (0, 24)); out.paste(cb, (8, 24)); out.paste(rb, (136, 24))
    OUT.parent.mkdir(parents=True, exist_ok=True)
    out.save(OUT)
    print("stitched", out.size, "->", OUT)


if __name__ == "__main__":
    main()
