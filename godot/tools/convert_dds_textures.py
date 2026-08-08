import bpy, os

# Resolve paths relative to this script (godot/tools/), so it works on any
# machine without hardcoded absolute paths. Requires the upstream 0 A.D.
# checkout to be reachable via the binaries/ junction at the repo root
# (see tools/setup-upstream-junctions.ps1 and AGENTS.md).
_TOOLS_DIR = os.path.dirname(os.path.abspath(__file__))
_GODOT_DIR = os.path.dirname(_TOOLS_DIR)          # godot/
_REPO_ROOT = os.path.dirname(_GODOT_DIR)          # repo root

SRC = os.path.join(_REPO_ROOT, "binaries", "data", "mods", "public", "art", "textures")
DST_ROOT = os.path.join(_GODOT_DIR, "assets", "textures")

def target_dir(rel):
    if "skins/skeletal/" in rel: return DST_ROOT
    if "skins/structural/" in rel: return os.path.join(DST_ROOT, "structural")
    if "skins/gaia/" in rel: return os.path.join(DST_ROOT, "gaia")
    if "skins/props/" in rel: return os.path.join(DST_ROOT, "props")
    if "terrain/types/" in rel:
        # 保留 types/<biome>/ 子结构:同名 basename 跨 biome 冲突(63 组,
        # 如 cliff_01/grass_01),拍平会互相覆盖、地图拿到错 biome 的贴图。
        return os.path.join(DST_ROOT, os.path.dirname(rel))
    return os.path.join(DST_ROOT, "misc")

count, skipped, failed = 0, 0, []
for root, _, files in os.walk(SRC):
    for f in sorted(files):
        if not f.lower().endswith(".dds"):
            continue
        png = os.path.splitext(f)[0] + ".png"
        rel = os.path.relpath(os.path.join(root, f), SRC).replace(os.sep, "/")
        dstdir = target_dir(rel)
        os.makedirs(dstdir, exist_ok=True)
        dst = os.path.join(dstdir, png)
        # 已转换判定按目标全路径(全局 basename 判重会误跳同名跨 biome 文件)。
        if os.path.exists(dst):
            skipped += 1
            continue
        try:
            img = bpy.data.images.load(os.path.join(root, f))
            _ = img.pixels[0]
            img.filepath_raw = dst
            img.file_format = 'PNG'
            img.save()
            bpy.data.images.remove(img)
            count += 1
        except Exception as e:
            failed.append(f"{rel}: {e}")

print(f"CONVERTED {count} SKIPPED {skipped} FAILED {len(failed)}")
for x in failed[:10]:
    print("FAILED", x)
