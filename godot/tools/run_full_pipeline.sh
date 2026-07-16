#!/bin/sh
# Full asset pipeline — run from the godot/ directory.
# Converts ALL meshes via Blender 4.2 + copies ALL textures.
#
# Usage: sh tools/run_full_pipeline.sh
#
# Requirements:
#   - Blender 4.2 LTS at /Applications/Blender 4.2 LTS.app
#   - 0 A.D. repo at a sibling directory (../binaries/data/mods/public/art)

set -e

BL="/Applications/Blender 4.2 LTS.app/Contents/MacOS/Blender"
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
            --max 200 2>&1 | grep -E "Found|Done|FAIL|SKIP" | head -5
    fi
done

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

# DDS → PNG conversion (for key textures that are DDS-only)
echo ""
echo ">>> Converting DDS textures to PNG..."
dds_count=0
for dds_dir in "$SRC/textures/skins/skeletal/athen" "$SRC/textures/skins/props/head" "$SRC/textures/terrain/types"; do
    if [ -d "$dds_dir" ]; then
        for dds in "$dds_dir"/*.dds; do
            [ -f "$dds" ] || continue
            name=$(basename "$dds" .dds)
            out="$TEX_DST/${name}.png"
            if [ ! -f "$out" ]; then
                sips -s format png "$dds" --out "$out" >/dev/null 2>&1 && dds_count=$((dds_count+1))
            fi
        done
    fi
done
echo "  converted $dds_count DDS files to PNG"

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
