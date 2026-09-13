"""Locate the repo and the ZeroAD.RlHost executable."""

from __future__ import annotations

import os
from pathlib import Path


def find_repo_root() -> Path:
    """Walk up from this file until ``ZeroAD.RlHost.csproj`` is found."""
    here = Path(__file__).resolve()
    for parent in here.parents:
        if (parent / "src" / "ZeroAD.RlHost" / "ZeroAD.RlHost.csproj").is_file():
            return parent
    msg = "ZeroAD.RlHost.csproj not found above python/zeroad_env"
    raise FileNotFoundError(msg)


def host_command(repo: Path | None = None) -> list[str]:
    """Return argv prefix that launches RlHost (dll or ``dotnet run``)."""
    root = repo or find_repo_root()
    override = os.environ.get("ZEROAD_RL_HOST")
    if override:
        return [override]
    dll = root / "src" / "ZeroAD.RlHost" / "bin" / "Release" / "net8.0" / "ZeroAD.RlHost.dll"
    debug = root / "src" / "ZeroAD.RlHost" / "bin" / "Debug" / "net8.0" / "ZeroAD.RlHost.dll"
    target = dll if dll.is_file() else debug
    if target.is_file():
        return ["dotnet", str(target)]
    return ["dotnet", "run", "--project", str(root / "src" / "ZeroAD.RlHost"), "--"]
