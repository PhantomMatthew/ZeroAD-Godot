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
# obs["entities"] int32 [512, 13]
# obs["spatial"]  int32 [4, 64, 64]
# obs["scalars"]  int32 [11]
# obs["function_mask"] uint8
t = torch.from_numpy(obs["entities"])  # policy / pointer mask in PyTorch
obs, reward, terminated, truncated, info = env.step({"function": 0})
env.close()
```

Vectorized shm (N parallel matches in one host process):

```python
from zeroad_env.shm import ZeroADShmEnv
vec = ZeroADShmEnv(n_slots=4, seed=1)
obs = vec.reset()  # entities [4, 512, 13]
```

## Remote/debug path (gRPC)

```python
env = make_env(backend="grpc", seed=1, privileged=True)
obs, _ = env.reset()
obs, r, done, trunc, info = env.step({"function": 0})
env.close()
```

Host flags: `--shm PATH` (default training) or `--grpc PORT` (`0` = ephemeral).
Do not put rollout traffic on gRPC unless the learner is on another machine.

## Layout

Constants in `layout.py` must match `src/ZeroAD.Sim/RL/ShmLayout.cs`
(`MAGIC=0x5A414452`, `VERSION=1`). Mismatch aborts `reset`.
