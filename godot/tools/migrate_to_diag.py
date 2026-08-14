#!/usr/bin/env python3
"""批量迁移 godot/Scripts 的 GD.Print/PrintErr/PushWarning → ZeroAD.Sim.Diag。

规则:
- GD.Print($"[Tag] msg")   → Diag.Log("Tag", $"msg")
- GD.PrintErr($"[Tag] msg") → Diag.Err("Tag", $"msg")
- GD.PushWarning($"[Tag] msg") → Diag.Warn("Tag", $"msg")
- 无 [Tag] 前缀的 → Diag.Log(FILE_TAG, ...)(tag 按文件映射)

只处理单行调用(参数在同一行)。跨行调用报告出来人工处理。
"""
from __future__ import annotations
import re, sys, os
from pathlib import Path

# 文件名 → 默认 tag(无前缀调用用)
FILE_TAG = {
    "Main.cs": "Main", "SimBridge.cs": "Sim", "MultiplayerController.cs": "MP",
    "SaveGameManager.cs": "SaveGame", "ActorParser.cs": "Actor", "SplatBaker.cs": "Terrain",
    "ReplayDriver.cs": "Replay", "TerrainRenderer.cs": "Terrain", "ReplayRecorder.cs": "Replay",
    "ReplayFileManager.cs": "Replay", "StructreePanel.cs": "Structree", "MapSceneBuilder.cs": "Map",
    "MapPreview.cs": "Map", "Localization.cs": "Localization", "StatePropSwitcher.cs": "Actor",
    "ActorComposer.cs": "Actor", "ScenarioMapLoader.cs": "Map", "CivInfoPanel.cs": "CivInfo",
    "OptionsCatalog.cs": "Options", "HotkeyCatalog.cs": "Hotkeys", "DefaultConfig.cs": "Options",
    "MapEnvironment.cs": "Map", "MainMenu.cs": "Main", "LoadingOverlay.cs": "Main",
    "AssetPathResolver.cs": "Actor", "ActorLoader.cs": "Actor",
}

SKIP = {"DiagGodot.cs", "DiagPanel.cs", "ActorDiagnostics.cs"}

CALL_RE = re.compile(
    r'(?P<indent>[ \t]*)GD\.(?P<method>PrintErr|PushWarning|Print)\((?P<args>.*)\)\s*;(?P<tail>[ \t]*)$'
)
TAG_PREFIX_RE = re.compile(r'\$?"\[(?P<tag>[A-Za-z0-9_-]+)\]\s*(?P<rest>.*)$')
LEVEL = {"Print": "Log", "PrintErr": "Err", "PushWarning": "Warn"}
DIAG = "ZeroAD.Sim.Diag"


def split_args(argstr: str) -> str:
    """取参数串(去掉尾部分号已由 CALL_RE 处理)。返回完整参数字符串。"""
    return argstr


def convert_line(line: str, file_tag: str):
    """返回 (新行, 是否改了, 备注)。"""
    m = CALL_RE.search(line)
    if not m:
        # 跨行调用(含 GD.Print 但不在单行结尾)
        if re.search(r'GD\.(PrintErr|PushWarning|Print)\(', line):
            return line, False, "MULTILINE?"
        return line, False, None
    method = m.group("method")
    args = split_args(m.group("args")).strip()
    indent = m.group("indent")
    level = LEVEL[method]

    # 提取 [Tag] 前缀
    tm = TAG_PREFIX_RE.match(args)
    if tm:
        tag = tm.group("tag")
        rest = tm.group("rest")
        # args 形如 $"[Tag] rest ..."  → 重组成 $"rest ..."
        # 原 args 以 $"/" 开头
        prefix_dollar = args.startswith('$"')
        newargs = ('$"' if prefix_dollar else '"') + rest
        newline = f"{indent}{DIAG}.{level}(\"{tag}\", {newargs});"
        return newline, True, f"tag={tag}"
    else:
        newline = f"{indent}{DIAG}.{level}(\"{file_tag}\", {args});"
        return newline, True, f"default-tag={file_tag}"


def main():
    root = Path("godot/Scripts")
    report = []
    for path in sorted(root.rglob("*.cs")):
        if path.name in SKIP:
            continue
        fname = path.name
        text = path.read_text()
        lines = text.split("\n")
        out = []
        changed = 0
        notes = []
        for ln in lines:
            newln, did, note = convert_line(ln, FILE_TAG.get(fname, "Main"))
            out.append(newln)
            if did:
                changed += 1
                if note: notes.append(note)
            elif note:
                notes.append(f"SKIPPED: {ln.strip()[:60]}")
        if changed > 0:
            path.write_text("\n".join(out))
            report.append((str(path), changed, notes))
    for p, c, notes in report:
        print(f"{p}: {c} converted")
        for n in notes:
            if n.startswith("SKIPPED") or "MULTILINE" in n:
                print(f"    !! {n}")
    print(f"\nTotal files changed: {len(report)}, total conversions: {sum(c for _,c,_ in report)}")


if __name__ == "__main__":
    main()
