<#
.SYNOPSIS
    在本 repo 根目录建立指向 0 A.D. 上游 checkout 的 directory junction。

.DESCRIPTION
    C# 重写不再把原版 0 A.D. 的 binaries/ build/ libraries/ source/ 纳入版本控制
    （它们是 C++ 引擎相关，体积巨大且 C# 重写不需要）。本脚本在 repo 根建立
    Windows directory junction，把这四个名字指向本机的上游 checkout，使 C# 代码
    里基于相对路径的解析逻辑（../binaries、向上查找 binaries/）无需任何改动即可
    透明地读到上游数据。

    Junction 选择而非 symlink：junction 不需要管理员权限、不依赖 Developer Mode，
    在 NTFS 上对应用层完全透明。

.PARAMETER Upstream
    上游 0 A.D. checkout 的根路径。默认 C:\SourceCode\0ad。
    可用 -Upstream 覆盖，或设环境变量 ZEROAD_UPSTREAM。

.EXAMPLE
    # 默认路径（C:\SourceCode\0ad）
    powershell -ExecutionPolicy Bypass -File tools/setup-upstream-junctions.ps1

.EXAMPLE
    # 自定义上游路径
    powershell -ExecutionPolicy Bypass -File tools/setup-upstream-junctions.ps1 -Upstream D:\code\0ad

.NOTES
    幂等：可重复运行，已存在的 junction 会被重建（指向最新 -Upstream）。
    跨平台：macOS/Linux 等价命令见 AGENTS.md。
#>

[CmdletBinding()]
param(
    [string]$Upstream
)

# 上游路径解析：参数 > 环境变量 > 默认值
if (-not $Upstream) { $Upstream = $env:ZEROAD_UPSTREAM }
if (-not $Upstream) { $Upstream = 'C:\SourceCode\0ad' }

$ErrorActionPreference = 'Stop'

# repo 根 = 本脚本所在 tools/ 目录的上一级
$repoRoot = Split-Path -Parent $PSScriptRoot

Write-Host '============================================' -ForegroundColor DarkGray
Write-Host '  建立 0 A.D. 上游 junction' -ForegroundColor Cyan
Write-Host '============================================' -ForegroundColor DarkGray
Write-Host "上游 checkout : $Upstream"
Write-Host "本 repo 根   : $repoRoot"
Write-Host ''

# 校验上游根存在
if (-not (Test-Path $Upstream -PathType Container)) {
    Write-Error "上游 checkout 不存在: $Upstream`n请用 -Upstream 指定正确路径，或设 ZEROAD_UPSTREAM 环境变量。"
    exit 1
}

# 要建立的 junction 列表（每个都是 upstream 下的同名目录）
$junctions = @('binaries', 'build', 'libraries', 'source')
$ok = 0; $skip = 0; $fail = 0

foreach ($name in $junctions) {
    $link = Join-Path $repoRoot $name
    $target = Join-Path $Upstream $name

    # 校验上游对应目录存在
    if (-not (Test-Path $target -PathType Container)) {
        Write-Host "[SKIP] $name —— 上游缺失 $target" -ForegroundColor Yellow
        $skip++; continue
    }

    # 若链接位已存在（普通目录/junction/symlink），先清理
    if (Test-Path $link) {
        $item = Get-Item $link -Force
        if ($item.LinkType) {
            # 已是 junction/symlink：删链接不影响 target
            cmd /c rmdir `"$link`" | Out-Null
        } else {
            # 是普通目录（可能含文件）：不擅自动，提示用户
            Write-Host "[FAIL] $name —— $link 是普通目录而非 junction，请手动确认后删除再运行" -ForegroundColor Red
            $fail++; continue
        }
    }

    try {
        New-Item -ItemType Junction -Path $link -Target $target | Out-Null
        Write-Host "[OK]   $name -> $target" -ForegroundColor Green
        $ok++
    } catch {
        Write-Host "[FAIL] $name —— $($_.Exception.Message)" -ForegroundColor Red
        $fail++
    }
}

Write-Host ''
Write-Host "完成: 成功 $ok, 跳过 $skip, 失败 $fail" -ForegroundColor Cyan
if ($fail -gt 0) { exit 1 }
