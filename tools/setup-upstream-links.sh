#!/usr/bin/env bash
# =============================================================================
# setup-upstream-links.sh — 在本 repo 根目录建立指向 0 A.D. 上游 checkout 的软链接
# (macOS / Linux 版;Windows 用 tools/setup-upstream-junctions.ps1)。
#
# C# 重写不把原版的 binaries/ build/ libraries/ source/ 纳入版本控制(体积巨大)。
# 本脚本把这四个名字软链到本机的上游 checkout,C# 代码里基于相对路径的解析
# (../binaries、向上查找 binaries/)无需任何改动即可透明读到上游数据。
#
# 上游路径解析优先级:命令行参数 > $ZEROAD_UPSTREAM > 平台默认/常见位置探测。
#
# 用法:
#   tools/setup-upstream-links.sh                      # 默认/探测
#   tools/setup-upstream-links.sh /path/to/0ad         # 显式指定上游位置
#   ZEROAD_UPSTREAM=/path/to/0ad tools/setup-upstream-links.sh
#
# 幂等:可重复运行,已存在的软链接会被重建(指向最新参数);普通目录不动(报 FAIL)。
# =============================================================================
set -euo pipefail

# 防呆:Git Bash/MSYS/Cygwin 的 ln -s 默认是"整树复制"(或需开发者模式的
# 原生 symlink)——在本 repo 根会静默复制上游几十 GB。Windows 必须走
# junction(PowerShell 脚本),此处直接拒绝并指路。
case "$(uname -s 2>/dev/null || echo unknown)" in
    MINGW*|MSYS*|CYGWIN*)
        echo "检测到 Windows 上的类 Unix shell——请改用 PowerShell 脚本:" >&2
        echo "  powershell -ExecutionPolicy Bypass -File tools/setup-upstream-junctions.ps1" >&2
        echo "(junction 无需管理员/开发者模式,且对应用层完全透明)" >&2
        exit 1
        ;;
esac

# repo 根 = 本脚本所在 tools/ 目录的上一级
repoRoot="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# 上游路径解析:参数 > 环境变量 > 默认/探测
upstream="${1:-${ZEROAD_UPSTREAM:-}}"
if [ -z "$upstream" ]; then
    candidates=(
        "$HOME/SourceCode/gitea/0ad"    # macOS 已知检出点(AGENTS.md)
        "$HOME/SourceCode/0ad"
        "$HOME/src/0ad"
        "/Users/matthew/SourceCode/gitea/0ad"
    )
    for c in "${candidates[@]}"; do
        if [ -d "$c" ]; then upstream="$c"; break; fi
    done
fi

echo "============================================"
echo "  建立 0 A.D. 上游软链接 (macOS/Linux)"
echo "============================================"
echo "上游 checkout : ${upstream:-<未指定>}"
echo "本 repo 根   : $repoRoot"
echo ""

if [ -z "$upstream" ] || [ ! -d "$upstream" ]; then
    echo "错误: 上游 checkout 未找到: '${upstream:-}'" >&2
    echo "请用参数指定: tools/setup-upstream-links.sh /path/to/0ad" >&2
    echo "或设环境变量: ZEROAD_UPSTREAM=/path/to/0ad" >&2
    exit 1
fi

ok=0; skip=0; fail=0
for name in binaries build libraries source; do
    link="$repoRoot/$name"
    target="$upstream/$name"

    # 校验上游对应目录存在
    if [ ! -d "$target" ]; then
        echo "[SKIP] $name —— 上游缺失 $target"
        skip=$((skip+1)); continue
    fi

    # 已是软链接:删掉重建(ln -s 指向新 target;删链接不影响目标)
    if [ -L "$link" ]; then
        rm -f "$link"
    elif [ -e "$link" ]; then
        # 普通目录/文件:不擅自动,提示手动处理
        echo "[FAIL] $name —— $link 是普通目录/文件而非软链接,请手动确认后删除再运行"
        fail=$((fail+1)); continue
    fi

    if ln -s "$target" "$link"; then
        echo "[OK]   $name -> $target"
        ok=$((ok+1))
    else
        echo "[FAIL] $name —— ln -s 失败"
        fail=$((fail+1))
    fi
done

echo ""
echo "完成: 成功 $ok, 跳过 $skip, 失败 $fail"
[ "$fail" -gt 0 ] && exit 1
exit 0
