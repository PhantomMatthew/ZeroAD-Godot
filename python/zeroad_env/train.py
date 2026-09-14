"""Masked REINFORCE loop against the headless ZeroAD encounter (shm + Petra)."""

from __future__ import annotations

import argparse
from collections.abc import Mapping

import numpy as np

from zeroad_env.gym_env import ZeroADGymEnv
from zeroad_env import layout as L


def _own_attacker(obs: Mapping[str, np.ndarray]) -> int:
    ent = obs["entities"]
    for i in range(L.MAX_ENTITIES):
        if ent[i, 0] == 0:
            continue
        if ent[i, 1] == 1 and (int(ent[i, 8]) & 2) != 0:
            return i
    return -1


def _first_enemy(obs: Mapping[str, np.ndarray]) -> int:
    ent = obs["entities"]
    for i in range(L.MAX_ENTITIES):
        if ent[i, 0] == 0:
            continue
        if ent[i, 1] == 3:
            return i
    return -1


def _features(obs: Mapping[str, np.ndarray]) -> np.ndarray:
    ent = obs["entities"].astype(np.float32)
    valid = ent[:, 0:1]
    count = float(np.maximum(valid.sum(), 1.0))
    pooled = (ent * valid).sum(axis=0) / count
    scalars = obs["scalars"].astype(np.float32)
    return np.concatenate([pooled, scalars], axis=0)


def train(episodes: int, max_steps: int, seed: int, lr: float, privileged: bool) -> None:
    """Run a tiny masked-policy trainer. Requires PyTorch."""
    import torch
    from torch import nn
    from torch.distributions import Categorical

    env = ZeroADGymEnv(
        backend="shm",
        seed=seed,
        privileged=privileged,
        step_mul=8,
        petra=True,
        max_turns=10_000,
    )
    feat_dim = L.ENTITY_FEAT + L.SCALAR_COUNT
    policy = nn.Sequential(
        nn.Linear(feat_dim, 64),
        nn.Tanh(),
        nn.Linear(64, 10),
    )
    opt = torch.optim.Adam(policy.parameters(), lr=lr)
    returns: list[float] = []
    try:
        for ep in range(episodes):
            obs, _info = env.reset()
            logps: list[torch.Tensor] = []
            ep_ret = 0.0
            terminated = False
            for _ in range(max_steps):
                x = torch.from_numpy(_features(obs)).unsqueeze(0)
                logits = policy(x).squeeze(0)
                mask = torch.from_numpy(obs["function_mask"][:10].astype(np.float32))
                logits = logits + (mask - 1.0) * 1e9
                dist = Categorical(logits=logits)
                fn = dist.sample()
                logps.append(dist.log_prob(fn))
                action = {
                    "function": int(fn.item()),
                    "selected": _own_attacker(obs),
                    "target": _first_enemy(obs),
                    "cell_x": 32,
                    "cell_z": 32,
                    "catalog": 0,
                }
                obs, reward, terminated, _trunc, _info = env.step(action)
                ep_ret += float(reward)
                if terminated:
                    break
            if logps:
                loss = -(torch.stack(logps).sum() * torch.tensor(ep_ret))
                opt.zero_grad()
                loss.backward()
                opt.step()
            returns.append(ep_ret)
            print(
                f"episode {ep + 1}/{episodes} return={ep_ret:.1f} "
                f"done={terminated} mean={float(np.mean(returns)):.3f}"
            )
    finally:
        env.close()


def main() -> None:
    """CLI entry."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--episodes", type=int, default=8)
    parser.add_argument("--steps", type=int, default=64)
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--lr", type=float, default=1e-3)
    parser.add_argument("--fog", action="store_true", help="disable privileged vision")
    args = parser.parse_args()
    train(args.episodes, args.steps, args.seed, args.lr, privileged=not args.fog)


if __name__ == "__main__":
    main()
