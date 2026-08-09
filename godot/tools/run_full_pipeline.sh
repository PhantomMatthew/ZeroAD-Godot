#!/bin/sh
# Full asset pipeline — run from the godot/ directory.
# Converts ALL meshes via Blender 4.2 + copies ALL textures.
#
# Usage: sh tools/run_full_pipeline.sh
#
# Requirements:
#   - Blender 4.2 LTS. Set BLender path via $BLENDER env var, or let the
#     script auto-detect the default install location for your OS:
#       macOS : /Applications/Blender 4.2 LTS.app/Contents/MacOS/Blender
#       Windows: "C:/Program Files/Blender Foundation/Blender 4.2/blender.exe"
#   - 0 A.D. upstream data at ../binaries/data/mods/public/art
#     (provided by the binaries/ junction — see tools/setup-upstream-junctions.ps1)

set -e

# Resolve Blender: $BLENDER env var > platform default locations.
if [ -n "$BLENDER" ]; then
    BL="$BLENDER"
elif [ -x "/Applications/Blender 4.2 LTS.app/Contents/MacOS/Blender" ]; then
    BL="/Applications/Blender 4.2 LTS.app/Contents/MacOS/Blender"
elif [ -x "C:/Program Files/Blender Foundation/Blender 4.2/blender.exe" ]; then
    BL="C:/Program Files/Blender Foundation/Blender 4.2/blender.exe"
else
    echo "ERROR: Blender 4.2 not found. Set BLENDER env var to its path." >&2
    exit 1
fi

SCRIPT="$(dirname "$0")/convert_all_assets.py"
SRC="../binaries/data/mods/public/art"
OUT="assets"

echo "============================================"
echo "  0 A.D. → Godot Full Asset Pipeline"
echo "============================================"

# ---- 1. Meshes ----
echo ""
echo ">>> Converting meshes (DAE → GLB)..."

for category in gaia structural skeletal/new props/new props/special props/helmet temp; do
    if [ -d "$SRC/meshes/$category" ]; then
        echo "  --- $category ---"
        "$BL" --background --python "$SCRIPT" -- \
            --input "$SRC/meshes/$category" \
            --output "$OUT/meshes/$(basename $(dirname $category))/$(basename $category)" \
            --max 99999 2>&1 | grep -E "Found|Done|FAIL|SKIP" | head -5
    fi
done

# ---- 1b. Repair unit-scale regression ----
# Blender's Collada import honors the DAE <unit meter="X"/> declaration, but the
# 0 A.D. engine IGNORES it (raw coords are game meters). Props authored in
# centimeters (0.01) / inches (0.0254) therefore bake a shrinking scale onto the
# GLB root node — heads render at 1% size, buildings at 0.7m instead of 28m.
# This must run AFTER the DAE→GLB conversion so future regenerations don't
# silently regress (godot/assets/ is gitignored, so the fix is otherwise lost).
echo ""
echo ">>> Repairing DAE <unit> scale regression..."
python3 "$(dirname "$0")/fix_glb_unit_scale.py" \
    --meshes-root "$OUT/meshes" \
    --dae-root "$SRC/meshes" 2>&1 | grep -E "^(fixed|would-fix):" || true

# ---- 2. Textures ----
echo ""
echo ">>> Copying textures..."

TEX_DST="$OUT/textures"

# Skins (units)
mkdir -p "$TEX_DST/skins"
find "$SRC/textures/skins/skeletal" -name "*.png" ! -name "*_norm*" ! -name "*_spec*" \
    -exec cp {} "$TEX_DST/skins/" \; 2>/dev/null
echo "  skins: $(ls "$TEX_DST/skins/"*.png 2>/dev/null | wc -l | tr -d ' ') files"

# Structural
mkdir -p "$TEX_DST/structural"
find "$SRC/textures/skins/structural" -name "*.png" \
    -exec cp {} "$TEX_DST/structural/" \; 2>/dev/null
echo "  structural: $(ls "$TEX_DST/structural/"*.png 2>/dev/null | wc -l | tr -d ' ') files"

# Gaia
mkdir -p "$TEX_DST/gaia"
find "$SRC/textures/skins/gaia" -name "*.png" \
    -exec cp {} "$TEX_DST/gaia/" \; 2>/dev/null
echo "  gaia: $(ls "$TEX_DST/gaia/"*.png 2>/dev/null | wc -l | tr -d ' ') files"

# Props (heads, weapons)
mkdir -p "$TEX_DST/props"
find "$SRC/textures/skins/props" -name "*.png" \
    -exec cp {} "$TEX_DST/props/" \; 2>/dev/null
echo "  props: $(ls "$TEX_DST/props/"*.png 2>/dev/null | wc -l | tr -d ' ') files"

# Terrain
mkdir -p "$TEX_DST/terrain"
find "$SRC/textures/terrain/types" -name "*.png" \
    -exec cp {} "$TEX_DST/terrain/" \; 2>/dev/null
echo "  terrain: $(ls "$TEX_DST/terrain/"*.png 2>/dev/null | wc -l | tr -d ' ') files"

# DDS → PNG conversion (textures that are DDS-only). sips cannot read these DDS
# (DXT-compressed) — silently produced nothing. Blender 4.2 batch-imports them
# instead; convert_dds_textures.py mirrors this script's flat layout and skips
# basenames that already exist as PNG.
echo ""
echo ">>> Converting DDS textures to PNG (Blender)..."
"$BL" --background --python "$(dirname "$0")/convert_dds_textures.py" 2>&1 \
    | grep -E "CONVERTED|FAILED" || true

# ---- 3. UI textures ----
echo ""
echo ">>> Copying UI textures..."
UI_SRC="$SRC/textures/ui"
for ui_sub in pregame/backgrounds pregame/shell/logo global/button/button_stone_unselected.png global/button/button_stone_selected.png global/tile/stone_background.png session/icons/resources session/ribbon_bg.png session/minimap_circle_modern.png; do
    src_path="$UI_SRC/$ui_sub"
    if [ -f "$src_path" ]; then
        cp "$src_path" "$TEX_DST/../ui/" 2>/dev/null || true
    elif [ -d "$src_path" ]; then
        find "$src_path" -name "*.png" -exec cp {} "$TEX_DST/../ui/" \; 2>/dev/null || true
    fi
done
echo "  UI: $(ls "$OUT/ui/"*.png 2>/dev/null | wc -l | tr -d ' ') files"

# ---- Summary ----
echo ""
echo "============================================"
echo "  Pipeline Complete"
echo "============================================"
echo "  GLB meshes:    $(find "$OUT/meshes" -name "*.glb" 2>/dev/null | wc -l | tr -d ' ')"
echo "  Textures:      $(find "$TEX_DST" -name "*.png" 2>/dev/null | wc -l | tr -d ' ')"
echo "  UI assets:     $(find "$OUT/ui" -name "*.png" 2>/dev/null | wc -l | tr -d ' ')"
echo "============================================"
