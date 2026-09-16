# zeroad-env

Python client for the ZeroAD C# kernel RL API (AlphaStar-style entity table).
Simulation always runs in `ZeroAD.RlHost`. Training should use **shared memory**;
gRPC is a debug/remote facade over the same observation and action fields.

## Setup

Python 3.12+.

```bash
dotnet build src/ZeroAD.RlHost/ZeroAD.RlHost.csproj
pip install numpy
# optional: pip install gymnasium torch grpcio
export PYTHONPATH=python
```

The host loads a 1v1 encounter when staged data exists (`godot/export/data/mods/public`): civic centres, spearman, villagers, trees, `conquest_units` victory, and Petra on player 2 (`--petra`, default on). Catalog ids intern every public template and technology so Build/Train/Research actions resolve. Dummy seers are used only when that data tree is missing.

Regenerate the gRPC stubs after editing `src/ZeroAD.RlHost/Protos/zeroad_rl.proto`
(then change the `import zeroad_rl_pb2` line in `zeroad_rl_pb2_grpc.py` to
`from zeroad_env import zeroad_rl_pb2`):

```bash
pip install grpcio-tools
python -m grpc_tools.protoc -I src/ZeroAD.RlHost/Protos \
  --python_out=python/zeroad_env --grpc_python_out=python/zeroad_env zeroad_rl.proto
```

## Training path (shm, default)

```python
from zeroad_env import make_env
import torch

env = make_env(backend="shm", seed=1, privileged=False)
obs, info = env.reset()
# obs["entities"] int32 [512, 20]
# obs["spatial"]  int32 [5, 64, 64]
# obs["scalars"]  int32 [11]
# obs["function_mask"] uint8 [32]
# obs["entity_mask"] uint32 [512]
# obs["catalog"]      int32 [512, 3, 16]  # Train/Build/Research intern ids
t = torch.from_numpy(obs["entities"])  # policy / pointer mask in PyTorch
obs, reward, terminated, truncated, info = env.step({"function": 0})
env.close()
```

Vectorized shm (N parallel matches in one host process):

```python
from zeroad_env.shm import ZeroADShmEnv
vec = ZeroADShmEnv(n_slots=4, seed=1)
obs = vec.reset()  # entities [4, 512, 20]
```

## Remote/debug path (gRPC)

```python
env = make_env(backend="grpc", seed=1, privileged=True)
obs, _ = env.reset()
obs, r, done, trunc, info = env.step({"function": 0})
env.close()
```

Host flags: `--shm PATH` (default training) or `--grpc PORT` (`0` = ephemeral),
`--petra` / `--no-petra`, `--max-turns N`, `--step-mul N`.
Do not put rollout traffic on gRPC unless the learner is on another machine.

## First training loop

Pointer PPO (function head + selected/target entity pointers, value baseline,
clip 0.2). Layout **v4** has 32 functions, entity rows of 20 feats (carry / queue /
research), 5 spatial channels, 8 selected slots, per-entity masks, Train/Build/Research
intern lists (`catalog` 512×3×16), and a 128-byte action (agent 64 + opponent 64).
`n_slots>1` steps worlds **in parallel** (`Parallel.For` + thread-local
`SimSystem`). Host flags also include `--map NAME`, `--random-civs`, `--max-turns N`.

Requires a built host and `pip install torch`:

```bash
export PYTHONPATH=python
python -m zeroad_env.train --episodes 8 --steps 64 --curriculum --random-civs
python -m zeroad_env.train --algo impala --n-slots 4 --league /tmp/zeroad-league
python -m zeroad_env.eval_petra --episodes 8 --ckpt /tmp/zeroad-league/impala_0008.pt
```

`env.render()` returns a 64×64 RGB array from the visibility spatial channel
(`render_mode="rgb_array"`).

Self-play (same policy on both players, no Petra) writes a full opponent action
into the second 64-byte block (cells, catalog, up to 8 selected). Owner-rel
and the global function mask are rebuilt from the opponent's entity rows so the
pointer heads see "self" as player 2. Train actions copy the first own attacker
template id into `catalog`. Fan-out commands (Move, Attack, Gather, … plus Formation)
sample up to 8 distinct own units without replacement and put every filled slot
in the PPO log-prob; Train/Build/Research stay single-select.

```bash
python -m zeroad_env.train --episodes 8 --steps 64 --self-play
```

Barter drift is per-world (not process-global). pythonnet in-process loading is
not wired; C# `RlEnvironment.Step(agent, opponent)` is the in-process dual-action
API, and Python training stays on shm.

## Layout

Constants in `layout.py` must match `src/ZeroAD.Sim/RL/ShmLayout.cs`
(`MAGIC=0x5A414452`, `VERSION=4`). Mismatch aborts `reset`.
