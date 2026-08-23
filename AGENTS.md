# AGENTS.md

Guidance for AI agents working in this repo. Every line answers "would an agent likely miss this?"

## What this repo is

A **Godot 4.7.2 (.NET) + C# rewrite of 0 A.D.** (the open-source RTS). It is a hybrid repo with two distinct trees — knowing which one you are in is the single most important thing:

| Tree | Status | Purpose |
|---|---|---|
| `src/ZeroAD.Sim/` + `godot/` | **Active rewrite (C#)** | The new Godot game. Edit here. |
| `binaries/` `build/` `libraries/` `source/` | **Local junctions → `<0ad upstream>` (NOT tracked)** | C++ engine trees removed from this repo to slim it down. Provided locally as directory junctions pointing at the upstream checkout, so the C# rewrite reads data/assets verbatim and you can browse C++/JS reference code. See "Upstream junctions" below. |
| `<0ad upstream>` (external) | **Original 0 A.D. full source (C++20/JS), external reference** | The complete original engine source tree. This is the **authoritative reference** for porting behavior. **The path is machine-specific** — see below. |

The rewrite reads the original's data verbatim (entity templates XML, art assets) and ports its behavior to C#. Treat the original tree as a reference corpus, not a build target — unless explicitly asked to work on the C++ engine.

> **Where to find the original C++/JS source**: the upstream 0 A.D. tree is checked out **somewhere outside this repo**, and **its path differs per OS / machine** — it is NOT fixed. Known checkout locations:
>
> | OS | Path |
> |---|---|
> | **Windows** | `C:\SourceCode\0ad` |
> | **macOS** | `/Users/matthew/SourceCode/gitea/0ad` |
>
> Substitute your machine's path wherever this doc writes `<0ad upstream>`. When porting a subsystem, read the reference implementation there (e.g. `<0ad upstream>/source/simulation2/...`).

### Upstream junctions (set up once after cloning)

The C++ engine trees (`binaries/`, `build/`, `libraries/`, `source/`) are **removed from this repo** and provided locally as directory junctions pointing at `<0ad upstream>`. This keeps the C# rewrite's relative-path resolvers (`../binaries`, walk-up-to-find-`binaries/`) working unchanged while reading upstream data/assets verbatim.

**After a fresh clone**, create the links (one-time). Upstream path resolution order on both scripts: explicit argument > `ZEROAD_UPSTREAM` env var > platform default/probe (`C:\SourceCode\0ad` on Windows; `~/SourceCode/gitea/0ad` etc. on macOS/Linux):

```bash
# Windows (PowerShell) — directory junctions (no admin/Developer Mode needed)
powershell -ExecutionPolicy Bypass -File tools/setup-upstream-junctions.ps1

# macOS / Linux — symlinks (custom upstream location: pass it as the argument)
tools/setup-upstream-links.sh /Users/matthew/SourceCode/gitea/0ad
```

> Windows note: do NOT run the bash script under Git Bash/MSYS — its `ln -s` deep-copies the whole upstream tree (the script refuses). Use the PowerShell junction script there.

The junctions/symlinks are gitignored — they will never be tracked. If `<0ad upstream>` moves, just re-run the script.

**Master plan**: `godot-rewrite-plan.md` (modules M0–M10, milestones MS1–MS7, risk matrix). Read it before any non-trivial rewrite work.
**System deep-dive notes**: `claude-analyze/*.md` (15 docs analyzing the original engine: ECS, network lockstep, pathfinding, rendering, audio, UnitAI, etc.). The fastest way to understand a subsystem you're porting.

## Architecture of the rewrite

```
godot/                Godot presentation layer (non-deterministic, float OK)
  Scripts/SimBridge.cs  ← the ONLY seam between Godot and the sim kernel
        │ references
src/ZeroAD.Sim/       Deterministic simulation kernel (pure C#, zero Godot deps)
```

- **Entry flow**: `godot/project.godot` → `Scenes/Main.tscn` → `Scripts/Main.cs` (`Node3D`, namespace `ZeroAD.Godot`).
- `SimBridge` owns the `ComponentManager` / `TurnManager` and drives fixed-step ticks (≈10 Hz) from the Godot side. All sim state crosses this bridge.
- **Determinism is a hard constraint.** The kernel must stay Godot-free so it can run headless and be cross-platform hash-checked in CI. Never import `Godot.*` into `src/ZeroAD.Sim/`.

### Namespaces
- `ZeroAD.Sim.*` — kernel: `Components`, `Content`, `Events`, `Maths`, `Net`, `Templates`, `Triggers`, `Tutorial`, `Serialization`.
- `ZeroAD.Godot` — presentation (all of `godot/Scripts/`).
- `ZeroAD.Godot.Tools` — editor-side asset tooling (`godot/tools/`).

### Determinism rules (kernel only)
- Fixed-point math only — use `src/ZeroAD.Sim/Maths/Fixed.cs` (`CFixed_15_16`, ported from `source/maths/Fixed.h`). **No `float`/`double` in sim logic.**
- PRNG is `Rand48` (deterministic); never use `System.Random` or `DateTime`-seeded RNG in the kernel.
- `ZeroAD.Sim.csproj` sets `<ServerGarbageCollection>false</ServerGarbageCollection>` and `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` deliberately — do not change these casually.

## Build / test / run commands

There is **no `.sln` file**. Build each C# project by its `.csproj` path.

```bash
# Deterministic kernel (headless, no Godot needed)
dotnet build src/ZeroAD.Sim/ZeroAD.Sim.csproj
dotnet test  src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj     # xUnit; DeterminismTests, FixedTests, VectorTests, ParamNodeTests
# Run a single test class:
dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj --filter "FullyQualifiedName~DeterminismTests"

# Godot presentation (requires Godot .NET SDK environment)
dotnet build godot/GodotProject.csproj

# Run the game: open godot/project.godot in Godot 4.7.2 (.NET build) and press Play.
```

### Warnings are errors (both C# projects)
`ZeroAD.Sim.csproj` and `ZeroAD.Sim.Tests.csproj` both set `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`. `GodotProject.csproj` has `<Nullable>enable</Nullable>`. Do not silence warnings — fix them. No `as`-casts-to-`dynamic`, no pragma suppressions to sneak past this.

### Lint / format (these target the ORIGINAL 0 A.D. code, not the C# rewrite)
```bash
npm run lint        # eslint — lints JS in binaries/data/mods/ (Allman braces, tabs)
npm run lint:fix
ruff check          # Python — line-length 99, py311, select=ALL (see ruff.toml)
ruff format
```
The `eslint.config.mjs` and `ruff.toml` at root govern the **original** tree's JS/Python (mod scripts, tooling like `source/tools/entity/checkrefs.py`). They do **not** apply to `src/` or `godot/` C# code.

### Asset conversion pipeline (original → Godot)
Run from inside `godot/`:
```bash
sh tools/run_full_pipeline.sh
```
Requires **Blender 4.2 LTS** (set path via `$BLENDER` env var, or the script auto-detects default install locations for macOS/Windows) and the original 0 A.D. art at `../binaries/data/mods/public/art` (provided via the `binaries/` junction). Converts DAE→GLB meshes and copies/converts textures (PNG, DDS→PNG via Blender) into `godot/assets/`. Per-category/single-asset conversion: `godot/tools/convert_dae_to_gltf.py`, `convert_all_assets.py`, `build_animated_unit.py`. These are Python — `ruff` governs them.

## Where to find reference implementations

When porting a subsystem, start from the original code. **The original C++/JS source lives at `<0ad upstream>`** (external to this repo; `source/` in-repo is gitignored). Paths below are relative to that root:

| You're working on | Look at |
|---|---|
| Fixed-point math, vectors, trig | `source/maths/Fixed*.h`, `source/maths/...` |
| ECS: components, messages, entity lifecycle | `source/simulation2/system/` (C++) and `binaries/data/mods/public/simulation/components/*.js` (JS behavior — **UnitAI.js is ~6000 lines, the hardest port**) |
| Turn manager / lockstep / netcode | `source/simulation2/system/TurnManager*`, `source/network/` |
| Pathfinding (hierarchical + vertex) | `source/simulation2/helpers/HierarchicalPathfinder*`, `Pathfinding.h`, `VertexPathfinder*` |
| Entity template loading (XML inheritance/merge) | `source/simulation2/system/ParamNode` |
| Serialization + state hashing (OOS detection) | `source/simulation2/serialization/` |
| Rendering / map loading / actors | `source/renderer/`, `source/graphics/` |
| PMP map file format | `source/graphics/MapIO.h` (`FILE_VERSION`), `source/graphics/MapWriter.cpp` (header layout), `source/graphics/MapReader.cpp` |
| Reference integrity checker | `source/tools/entity/checkrefs.py` |

**Example**: to look up the PMP header format, read `<0ad upstream>/source/graphics/MapIO.h` and `MapWriter.cpp`.

Entity templates (data, consumed as-is by the rewrite): `binaries/data/mods/public/simulation/templates/*.xml` — **read via the `binaries/` junction**, not tracked in this repo. The C# rewrite resolves this path relatively (`../binaries/...` or walk-up from the test assembly), so the junction makes it transparent.

## Conventions that differ from defaults

- **Godot version is pinned to 4.7.2** (`Godot.NET.Sdk/4.7.2`, `project.godot` features `4.7`). Don't assume 4.x-generic APIs. Stay on the 4.7.x maintenance line; don't jump to 4.8 without an explicit decision.
- **Target framework is net8.0** across all three C# projects.
- **`ImplicitUsings` disabled** in the kernel — write explicit `using` directives.
- C# style follows `.claude/rules/csharp/` (nullable on, `record` for value models, `async/await` with `CancellationToken`). Run `dotnet format` if available.
- Allman braces + tabs are the original JS convention; the new C# may differ — match the surrounding `ZeroAD.*` file, not the JS.
- Art assets are large; original 0 A.D. uses git-lfs upstream. If a model file looks like text or a pointer, the LFS pull is incomplete.

## Things to verify before claiming done

**Compile gate (mandatory after EVERY edit, not just before commit):**

- Run `dotnet build` on **every** project you touched and confirm `0 Error(s)`:
  - `dotnet build src/ZeroAD.Sim/ZeroAD.Sim.csproj` for kernel changes,
  - `dotnet build godot/GodotProject.csproj` for anything under `godot/`.
- Check the **error/warning counts in the summary lines** (`0 Warning(s)  0 Error(s)`), never just the last lines of output — an error scrolled out of a short `tail` still fails the build. Grep for `error` explicitly when in doubt: `dotnet build ... 2>&1 | grep -E "error|Error\(s\)"`.
- Do this after **each** edit batch, before moving on. Do not assume a small edit compiles; do not commit or push a tree that doesn't build.
- `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj` passes — `DeterminismTests` is the canary for cross-platform hash stability.
- Any change touching `src/ZeroAD.Sim/` must not introduce `float`/`double` or `Godot.*` references into the kernel.

## Asset surgery gate (mandatory before ANY batch edit under `godot/assets/`)

`godot/assets/` is a gitignored build product — git history gives NO rollback for it. A batch
edit here is an irreversible operation on 5000+ files. The 2026-08-21 regression chain
(occluders → missing heads → dead animations → giant weapons, each from a批量 scale script
applied on an unverified rule) came from skipping this gate. Before touching assets:

1. **Snapshot first, always** — one command, before the edit, no exceptions:
   ```bash
   rsync -a godot/assets "../asset_backups/assets_$(date +%Y%m%d_%H%M%S)/"
   ```
   (`asset_backups/` at repo root is gitignored; keep it outside `godot/` so Godot never imports it.)
2. **Rollback is then trivial**: `rsync -a --delete <backup-dir>/ godot/assets/`, then
   `cd godot && /Applications/Godot_mono.app/Contents/MacOS/Godot --headless --path . --import`
   (or the Blender-equivalent import step on other machines).
3. **Never apply a blanket rule to many files without measuring first**. Size/scale edits must
   use the **full span** (`accessor.max − accessor.min` per axis, × node scale) — checking only
   `max` misses negative extents and under-reports long models by 2-3× (the giant-spear bug).
4. **Ground truth before scaling**: the DAE corpus is MIXED — some files honor their
   `<unit meter>` declaration, others are raw meters with a lying unit tag. There is no single
   conversion rule; verify per-class with an in-game measurement (runtime prop-scale dump or
   screenshot) before batch-applying. When in doubt, clamp by span class, don't "normalize".
5. **Repair scripts go in `godot/tools/`** (`fix_glb_unit_node_scale*.py`,
   `fix_glb_weapon_span.py`, `restore_glb_from_import_cache.gd`,
   `convert_dae_to_gltf_pycollada.py`) — future rebuilds must be reproducible from committed
   tools, not from memory. Note: local Blender builds may have no Collada importer (check
   `bpy.ops.import_scene.collada` exists before planning a Blender-based reconvert; the
   pycollada converter + import-cache restorer are the fallback pair).
