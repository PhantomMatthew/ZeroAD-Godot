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
    if "terrain/types/" in rel: return os.path.join(DST_ROOT, "terrain")
    return os.path.join(DST_ROOT, "misc")

# Existing png basenames anywhere under DST_ROOT (skip those).
existing = set()
for root, _, files in os.walk(DST_ROOT):
    for f in files:
        if f.lower().endswith(".png"):
            existing.add(f.lower())

count, skipped, failed = 0, 0, []
for root, _, files in os.walk(SRC):
    for f in sorted(files):
        if not f.lower().endswith(".dds"):
            continue
        png = os.path.splitext(f)[0] + ".png"
        if png.lower() in existing:
            skipped += 1
            continue
        rel = os.path.relpath(os.path.join(root, f), SRC).replace(os.sep, "/")
        dstdir = target_dir(rel)
        os.makedirs(dstdir, exist_ok=True)
        dst = os.path.join(dstdir, png)
        try:
            img = bpy.data.images.load(os.path.join(root, f))
            _ = img.pixels[0]
            img.filepath_raw = dst
            img.file_format = 'PNG'
            img.save()
            bpy.data.images.remove(img)
            existing.add(png.lower())
            count += 1
        except Exception as e:
            failed.append(f"{rel}: {e}")

print(f"CONVERTED {count} SKIPPED {skipped} FAILED {len(failed)}")
for x in failed[:10]:
    print("FAILED", x)
