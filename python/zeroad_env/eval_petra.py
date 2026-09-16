"""Evaluate a pointer policy against Petra: win rate, length, checkpoint."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

import numpy as np

from zeroad_env.catalog import Catalog
from zeroad_env.gym_env import ZeroADGymEnv
from zeroad_env.train import Policy, _load_policy, _sample_side


def evaluate(
    episodes: int,
    max_steps: int,
    seed: int,
    ckpt: str,
    max_turns: int,
    map_name: str,
    map_size: int,
    privileged: bool,
) -> dict[str, Any]:
    """Roll out frozen policy vs Petra; return summary metrics."""
    import torch
    from torch import nn

    env = ZeroADGymEnv(
        backend="shm",
        seed=seed,
        privileged=privileged,
        step_mul=8,
        petra=True,
        max_turns=max_turns,
        map_name=map_name,
        map_size=map_size,
        random_civs=False,
    )
    catalog = Catalog.load()
    policy = Policy(torch, nn, catalog)
    if ckpt:
        _load_policy(policy, Path(ckpt))
    wins = 0
    losses = 0
    timeouts = 0
    lengths: list[int] = []
    returns: list[float] = []
    try:
        for ep in range(episodes):
            obs, info = env.reset()
            ep_ret = 0.0
            steps = 0
            terminated = False
            for steps in range(1, max_steps + 1):
                rec, _ = _sample_side(policy, obs)
                obs, reward, terminated, _trunc, info = env.step(
                    {
                        "function": rec["fn"],
                        "selected": rec["selected"],
                        "target": rec["tgt"],
                        "cell_x": rec["cx"],
                        "cell_z": rec["cz"],
                        "catalog": rec["cat"],
                    }
                )
                ep_ret += float(reward)
                if terminated:
                    break
            lengths.append(int(info.get("turn", steps)))
            returns.append(ep_ret)
            won = int(obs["scalars"][8]) == 1
            lost = int(obs["scalars"][9]) == 1
            if won:
                wins += 1
            elif lost:
                losses += 1
            else:
                timeouts += 1
            print(
                f"eval {ep + 1}/{episodes} return={ep_ret:.1f} "
                f"turn={lengths[-1]} win={wins} loss={losses} timeout={timeouts}"
            )
    finally:
        env.close()
    n = max(1, episodes)
    summary = {
        "episodes": episodes,
        "wins": wins,
        "losses": losses,
        "timeouts": timeouts,
        "win_rate": wins / n,
        "mean_return": float(np.mean(returns) if returns else 0.0),
        "mean_turns": float(np.mean(lengths) if lengths else 0.0),
        "seed": seed,
        "ckpt": ckpt,
    }
    return summary


def main() -> None:
    """CLI: python -m zeroad_env.eval_petra --episodes 8 --ckpt path.pt"""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--episodes", type=int, default=8)
    parser.add_argument("--steps", type=int, default=256)
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--ckpt", default="")
    parser.add_argument("--max-turns", type=int, default=10_000)
    parser.add_argument("--map", dest="map_name", default="")
    parser.add_argument("--map-size", type=int, default=128)
    parser.add_argument("--privileged", action="store_true")
    parser.add_argument("--json-out", default="")
    args = parser.parse_args()
    summary = evaluate(
        args.episodes,
        args.steps,
        args.seed,
        args.ckpt,
        args.max_turns,
        args.map_name,
        args.map_size,
        args.privileged,
    )
    text = json.dumps(summary, indent=2)
    print(text)
    if args.json_out:
        Path(args.json_out).write_text(text + "\n")


if __name__ == "__main__":
    main()
