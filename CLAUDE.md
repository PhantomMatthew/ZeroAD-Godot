# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`AGENTS.md` is the long-form version of this file (upstream junction setup, per-subsystem reference table, asset pipeline). Read it before non-trivial porting work.

## What this repo is

A **Godot 4.7 (.NET) + C# rewrite of 0 A.D.** Two trees coexist:

- `src/ZeroAD.Sim/` + `godot/` — the active C# rewrite. Edit here.
- `binaries/` `build/` `libraries/` `source/` — untracked junctions/symlinks into an external 0 A.D. checkout (`<0ad upstream>`: `/Users/matthew/SourceCode/gitea/0ad` on macOS, `C:\SourceCode\0ad` on Windows). The rewrite reads its XML templates and art verbatim; its C++/JS is the authoritative behavioral reference.

After a fresh clone, create the links once: `tools/setup-upstream-links.sh <0ad upstream>` (macOS/Linux) or `powershell -File tools/setup-upstream-junctions.ps1` (Windows — do not run the bash script under Git Bash; its `ln -s` deep-copies the tree).

Plan of record: `godot-rewrite-plan.md` (modules M0–M10, milestones MS1–MS7). Subsystem deep-dives on the original engine: `claude-analyze/*.md`.

## Commands

No `.sln` — build each `.csproj` by path.

```bash
# Deterministic kernel (headless)
dotnet build src/ZeroAD.Sim/ZeroAD.Sim.csproj
dotnet test  src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj
dotnet test  src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj --filter "FullyQualifiedName~DeterminismTests"

# Presentation layer
dotnet build godot/GodotProject.csproj
# Run: open godot/project.godot in Godot 4.7 (.NET) and press Play.

# Asset conversion (run from godot/, needs Blender 4.2 LTS via $BLENDER)
sh tools/run_full_pipeline.sh
```

`npm run lint` (eslint) and `ruff check` target the **original** tree's JS/Python plus `godot/tools/*.py` — they do not apply to `src/` or `godot/Scripts/` C#.

Both `ZeroAD.Sim.csproj` and `ZeroAD.Sim.Tests.csproj` set `TreatWarningsAsErrors` — a warning fails the build. Fix, don't suppress.

## Architecture

```
godot/Scripts/          presentation (ZeroAD.Godot) — non-deterministic, float OK
  SimBridge.cs          ← the ONLY seam to the kernel; owns ComponentManager/TurnManager, ticks ~10 Hz
  Main.cs               Scenes/Main.tscn root; in-match orchestration (~3k lines)
  MainMenu.cs           Scenes/MainMenu.tscn — the main_scene; out-of-session pages
src/ZeroAD.Sim/         deterministic kernel (ZeroAD.Sim.*) — zero Godot deps
```

Out-of-session state travels through three autoloads (`GameLaunchConfig`, `MatchSummaryStore`, `UserConfig`), not process env vars. `Main._Ready` reads `GameLaunchConfig.Mode` to decide skirmish / load / tutorial.

### Determinism rules (kernel only)
- Fixed-point only: `src/ZeroAD.Sim/Maths/Fixed.cs`. No `float`/`double` in sim logic.
- PRNG is `Rand48`. Never `System.Random` or time-seeded RNG.
- Never `using Godot;` in `src/ZeroAD.Sim/`. The kernel must run headless for cross-platform hash checks.
- All player commands enter the sim through the lockstep command path (`SimCommandExecutor`), never by direct component mutation — this is what keeps SP AI and MP hash-identical.

### Conventions
- Godot pinned to **4.7**; target framework net8.0 everywhere; `ImplicitUsings` disabled in the kernel (write explicit usings).
- Renderer is **`gl_compatibility`** by design (`project.godot`). Consequences: custom shaders don't receive shadows, and transparent MUL blending is unreliable — terrain shadows come from splat baking, territory from a fog overlay. Don't "fix" this by switching to Forward+/Metal.
- The C++ world is left-handed relative to Godot's. `_worldRoot` carries `Scale.z = -1`; hang new world visuals under it and keep sim coordinates unmirrored. Only `RTSCamera` and `ScreenToWorld` cross that boundary; the minimap flips z independently.

## Traps that have cost time before

- **Rebuild the kernel before measuring anything.** Launching Godot from the CLI loads a prebuilt DLL and will not necessarily recompile `ZeroAD.Sim` — you can profile old code. Always `dotnet build` first.
- **Several test files resolve data via `"../../../binaries"`**, which lands in the project dir, not the repo root; those data-driven cases silently skip. A green run does not prove data paths were exercised.
- Save-file format is versioned; changing serialized component fields requires bumping the version and updating the loader. Serialization read order must match write order — object initializers can silently reorder writes.
- Godot's Metal driver crashes (SIGBUS in memmove) on large scenes; that is an engine bug, not a regression here. Use `--rendering-driver opengl3` for headless/CLI runs.
- Inside a Godot `Button`, child `Control`s default to `Stop` mouse filter and swallow clicks.
