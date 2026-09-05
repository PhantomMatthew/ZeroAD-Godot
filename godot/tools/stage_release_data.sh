#!/bin/bash
# stage_release_data.sh — 把运行时直读的上游数据子集拷贝成发行包数据目录。
#
# 背景:游戏运行时经 RuntimePaths 读取"数据根"(开发期 = ../binaries junction)。
# 发行包不带上游树,改为在可执行文件旁放一个 data/ 目录,布局与 binaries/data 一致:
#   <exe>/data/mods/public/{simulation,maps,audio,art(子集),gui,l10n,campaigns}
#   <exe>/data/mods/mod/          (modern UI / audio 回落层)
#   <exe>/data/config/default.cfg
#   <exe>/data/l10n/
# RuntimePaths.FindBinariesRoot() 会在 exe 旁找到它。
#
# 用法(从 godot/ 目录运行):
#   sh tools/stage_release_data.sh [源data目录] [目标目录]
# 默认:源 = ../binaries/data(junction),目标 = export/data
set -euo pipefail

SRC="${1:-../binaries/data}"
DST="${2:-export/data}"

if [ ! -d "$SRC/mods/public/simulation" ]; then
    echo "error: source '$SRC' does not look like binaries/data (no mods/public/simulation)" >&2
    echo "       pass the upstream data dir explicitly: sh tools/stage_release_data.sh /path/to/0ad/binaries/data" >&2
    exit 1
fi

# 让 Godot 编辑器永远不扫描导出/暂存目录(否则 export/data 会被当项目资源重复导入)
touch "$(dirname "$DST")/.gdignore"

mkdir -p "$DST/mods/public" "$DST/mods" "$DST/config"

# 注意:art 只拷运行时直读子集(网格/贴图主体已转换进 godot/assets/,不重带)。
PUBLIC_DIRS=(
    simulation        # 实体模板/科技/光环/文明/组件 JS(schema grammar)
    maps              # PMP + scenario XML + random 图 JSON/heightmap
    audio             # ogg + 声音组 XML(AudioManager 直读,未经 Godot 导入)
    art/actors        # actor XML(变体/材质定义)
    art/terrains      # 地形定义 XML
    art/particles     # 环境粒子 XML
    art/textures/terrain/alphamaps   # splat 形状图
    art/textures/animated/water      # 水面动画帧
    art/textures/skies               # 天空盒
    art/textures/ui                  # 肖像/小地图图标/按钮/背景
    art/textures/skins/props         # 战斗贴花
    gui               # credits/tips/userreport 文本(整体 3.5M,全拷)
    l10n              # 翻译 .po
    campaigns         # 战役 JSON
)

for d in "${PUBLIC_DIRS[@]}"; do
    if [ -d "$SRC/mods/public/$d" ]; then
        mkdir -p "$DST/mods/public/$(dirname "$d")"
        rsync -a --delete "$SRC/mods/public/$d" "$DST/mods/public/$(dirname "$d")/"
    else
        echo "warn: missing $SRC/mods/public/$d (skipped)"
    fi
done

# mods/mod 回落层(modern UI 贴图 + audio 回落,StructreePanel/AudioManager 用)
if [ -d "$SRC/mods/mod" ]; then
    rsync -a --delete "$SRC/mods/mod" "$DST/mods/"
fi

# data/config(default.cfg:音量/热键/全部默认配置)
if [ -d "$SRC/config" ]; then
    rsync -a --delete "$SRC/config" "$DST/"
fi

# data/l10n(引擎级翻译目录,与 public/l10n 并存)
if [ -d "$SRC/l10n" ]; then
    rsync -a --delete "$SRC/l10n" "$DST/"
fi

echo "staged: $DST"
du -sh "$DST"
