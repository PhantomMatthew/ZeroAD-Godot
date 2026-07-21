#!/usr/bin/env python3
"""Fix GLB files whose image URIs were polluted with Windows absolute paths.

Problem
-------
About 250 of the ~4350 GLBs under ``godot/assets/meshes`` carry image URIs like::

    C:\\Documents and Settings\\Brad\\My Documents\\0 A.D\\...\\tree_carob_b.dds

These leak in when Blender/assimp exports COLLADA to GLB and the source DAE stored
the texture as an absolute path from the original artist's machine. Godot's headless
importer logs an ERROR and skips, but the Metal/GPU import path on macOS crashes with
``EXC_BAD_ACCESS / SIGBUS`` inside ``IOGPUMetalResource`` (see DiagnosticReports).

Fix
---
Rewrite each affected GLB in place: parse the JSON chunk, replace any image URI that
is an absolute path (contains ``:`` or a backslash, i.e. clearly not a glTF relative
reference) with just its basename + ``.png``. That keeps geometry/skeletons intact,
gives Godot a sane (possibly-missing) texture reference to resolve via its normal
asset pipeline, and removes the malformed path that crashes the GPU driver.

GLB binary layout (Khronos spec):
    header:   magic(0x46546C67) u32 | version u32 | length u32      (12 bytes)
    chunk[0]: chunkLength u32 | chunkType u32(0x4E4F534A="JSON") | data (+ 0x00 pad to 4)
    chunk[1]: chunkLength u32 | chunkType u32(0x004E4942="BIN\0")  | data (+ 0x00 pad to 4)

Usage::

    python3 godot/tools/fix_glb_texture_uris.py            # fix all under godot/assets/meshes
    python3 godot/tools/fix_glb_texture_uris.py --check     # report only, no writes
    python3 godot/tools/fix_glb_texture_uris.py path/a.glb path/b.glb
"""
from __future__ import annotations

import json
import struct
import sys
from pathlib import Path

GLB_MAGIC = 0x46546C67  # "glTF"
JSON_CHUNK_TYPE = 0x4E4F534A  # "JSON"
BIN_CHUNK_TYPE = 0x004E4942  # "BIN\0"


def _is_absolute_path_uri(uri: str) -> bool:
    """True for Windows drive paths (``C:\\...``) or any backslash path — never a legal
    glTF relative URI. Data URIs (``data:...``) and plain filenames are left alone."""
    if uri.startswith("data:"):
        return False
    # Windows drive letter "X:" or any backslash marks an absolute/malformed path.
    return ":" in uri[:3] or "\\" in uri


def _sanitize_uri(uri: str) -> str:
    """Reduce a polluted absolute path to just its basename with a .png extension so
    Godot's asset resolver gets a normal (findable-or-missing) texture name."""
    # Take everything after the last separator (handle both / and \).
    base = uri.replace("\\", "/").rsplit("/", 1)[-1]
    if base.lower().endswith(".dds"):
        base = base[:-4] + ".png"
    return base


def fix_glb(path: Path, *, write: bool = True) -> tuple[bool, int]:
    """Return (changed, num_uris_fixed). Reads GLB, rewrites image URIs if needed."""
    data = path.read_bytes()
    if len(data) < 12:
        return False, 0

    magic, version, total_len = struct.unpack_from("<III", data, 0)
    if magic != GLB_MAGIC:
        return False, 0

    # Walk chunks. We only need the JSON chunk (first), but must copy BIN through.
    offset = 12
    chunks: list[tuple[int, bytes]] = []  # (chunkType, data)
    while offset + 8 <= len(data):
        chunk_len, chunk_type = struct.unpack_from("<II", data, offset)
        chunk_data = data[offset + 8 : offset + 8 + chunk_len]
        chunks.append((chunk_type, chunk_data))
        # Advance past data + 4-byte alignment padding.
        padded = (chunk_len + 3) & ~3
        offset += 8 + padded
        if offset >= total_len:
            break

    if not chunks or chunks[0][0] != JSON_CHUNK_TYPE:
        return False, 0

    json_bytes = chunks[0][1]
    # JSON chunk is padded with trailing 0x00 (spaces 0x20 also legal); strip them for parse.
    try:
        gltf = json.loads(json_bytes.decode("utf-8").rstrip("\x00 ").encode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return False, 0

    images = gltf.get("images")
    if not isinstance(images, list):
        return False, 0

    fixed = 0
    for img in images:
        uri = img.get("uri")
        if isinstance(uri, str) and _is_absolute_path_uri(uri):
            img["uri"] = _sanitize_uri(uri)
            fixed += 1

    if fixed == 0:
        return False, 0

    if not write:
        return True, fixed

    # Re-serialize. JSON chunk must be 4-byte aligned with 0x20 (space) padding.
    new_json = json.dumps(gltf, separators=(",", ":")).encode("utf-8")
    json_pad = (4 - (len(new_json) & 3)) & 3
    new_json += b" " * json_pad

    out = bytearray()
    # Header placeholder; total length filled after chunks.
    out += struct.pack("<III", GLB_MAGIC, version, 0)
    # JSON chunk.
    out += struct.pack("<II", len(new_json), JSON_CHUNK_TYPE)
    out += new_json
    # Remaining chunks (BIN etc.) carried through verbatim with their existing padding.
    for chunk_type, chunk_data in chunks[1:]:
        padded = (len(chunk_data) + 3) & ~3
        out += struct.pack("<II", len(chunk_data), chunk_type)
        out += chunk_data
        out += b"\x00" * (padded - len(chunk_data))

    struct.pack_into("<I", out, 8, len(out))  # total length
    path.write_bytes(bytes(out))
    return True, fixed


def main(argv: list[str]) -> int:
    args = [a for a in argv[1:] if not a.startswith("--")]
    check_only = "--check" in argv

    if args:
        targets = [Path(a) for a in args]
    else:
        meshes = Path("godot/assets/meshes")
        if not meshes.is_dir():
            # Run from repo root expected; fall back to absolute-ish discovery.
            meshes = Path(__file__).resolve().parents[2] / "assets" / "meshes"
        targets = sorted(meshes.rglob("*.glb"))

    changed = 0
    total_fixed = 0
    scanned = 0
    for glb in targets:
        if not glb.is_file():
            continue
        scanned += 1
        try:
            did, n = fix_glb(glb, write=not check_only)
        except Exception as exc:  # noqa: BLE001 - report and continue
            print(f"ERROR {glb}: {exc}", file=sys.stderr)
            continue
        if did:
            changed += 1
            total_fixed += n
            verb = "would fix" if check_only else "fixed"
            print(f"{verb}: {glb} ({n} URI)")

    mode = "checked" if check_only else "fixed"
    print(f"\n{mode} {scanned} GLBs; {changed} files {mode} ({total_fixed} URIs total).")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
