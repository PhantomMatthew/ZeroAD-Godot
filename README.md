# 0 A.D. — Godot / C# Rewrite

A ground-up rewrite of [0 A.D.](https://play0ad.com/) (the free, open-source RTS of ancient
warfare by Wildfire Games) on **Godot 4.7.2 (.NET) + C#**, targeting standalone exportable
packages (macOS / Linux / Windows).

The rewrite reads the original game's data verbatim (entity templates XML, art assets) and
ports its behavior to a **deterministic C# simulation kernel**. The original C++/JS engine is
not built here — it is consulted as the authoritative reference implementation.

## Repository layout

| Path | What it is |
|---|---|
| `src/ZeroAD.Sim/` | Deterministic simulation kernel — pure C#, **zero Godot dependencies**, fixed-point math only |
| `src/ZeroAD.Sim.Tests/` | xUnit tests (`DeterminismTests` is the cross-platform hash-stability canary) |
| `godot/` | Godot presentation layer (project root: `godot/project.godot`, entry `Scenes/MainMenu.tscn`) |
| `binaries/` `build/` `libraries/` `source/` | Local junctions/symlinks to an upstream 0 A.D. checkout (**not tracked**) — reference data + reference source |
| `godot-rewrite-plan.md` | Master plan (modules M0–M10, milestones, risk matrix) |
| `REMAINING-WORK.md` / `PORTING-GAPS.md` | Porting status / gap tracking |
| `claude-analyze/` | 15+ deep-dive notes on the original engine (ECS, lockstep netcode, pathfinding, UnitAI, …) |
| `AGENTS.md` | Contributor/agent guide — read first before non-trivial work |

## Quick start

Prerequisites: **.NET 8 SDK**, **Godot 4.7.2 (.NET/Mono build)**, and a local checkout of
upstream 0 A.D.

```bash
# 1. Link the upstream data tree (one-time; macOS/Linux shown, Windows: tools/setup-upstream-junctions.ps1)
tools/setup-upstream-links.sh /path/to/0ad

# 2. Models/animations (GLB) are tracked in git — a fresh clone already has them.
#    Textures are a build product; regenerate with Blender 4.2 LTS:
cd godot && sh tools/run_full_pipeline.sh

# 3. Build
dotnet build src/ZeroAD.Sim/ZeroAD.Sim.csproj        # kernel (headless, no Godot needed)
dotnet test  src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj
dotnet build godot/GodotProject.csproj               # presentation

# 4. Run: open godot/project.godot in Godot 4.7.2 (.NET) and press Play.
```

## Release packaging

```bash
cd godot
sh tools/stage_release_data.sh        # stage the runtime data subset into export/data/
/Applications/Godot_mono.app/Contents/MacOS/Godot --headless --path . --export-release "macOS"
```

Export presets for Linux/Windows/macOS live in `godot/export_presets.cfg`; outputs go to the
gitignored `godot/export/`. The packaged game resolves its data beside the executable via
`Scripts/RuntimePaths.cs` (`ZEROAD_DATA_DIR` env → `<exe>/data` → `.app` Resources → dev
junction). Known export gotchas (mono template dir, `.sln` requirement, ETC2/ASTC, GLSL `.cs`
collision…) are documented in `AGENTS.md` and `REMAINING-WORK.md` §7.

## Contributing

See `AGENTS.md` for architecture invariants (the kernel must stay Godot-free and
deterministic: fixed-point math, `Rand48` PRNG, no `System.Random`), build/test gates, and the
asset surgery gate for `godot/assets/`.

## License

Derived from 0 A.D. by Wildfire Games: original engine code is GPLv2+, art/audio assets are
CC-BY-SA 3.0. The same terms apply to the corresponding derived content in this repository.
Upstream: https://gitea.wildfiregames.com/0ad/0ad
