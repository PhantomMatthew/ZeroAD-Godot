"""Random NoOp rollout: verifies mmap reset/step against the C# host."""

from __future__ import annotations

from zeroad_env.gym_env import ZeroADGymEnv


def main() -> None:
    """Run a short random (NoOp) episode and print tensor shapes."""
    env = ZeroADGymEnv(seed=1, privileged=True)
    try:
        obs, _info = env.reset()
        print("entities", tuple(obs["entities"].shape), "spatial", tuple(obs["spatial"].shape))
        total = 0.0
        for _ in range(16):
            obs, reward, terminated, _truncated, info = env.step({"function": 0})
            total += reward
            if terminated:
                print("done at turn", info.get("turn"), "return", total)
                break
        else:
            print("return", total, "turn", info.get("turn"))
        # Torch is optional: training scripts do torch.from_numpy(obs["entities"]).
        try:
            import torch
        except ImportError:
            print("torch not installed; numpy arrays are ready for torch.from_numpy")
        else:
            t = torch.from_numpy(obs["entities"])
            print("torch entities", tuple(t.shape), t.dtype)
    finally:
        env.close()


if __name__ == "__main__":
    main()
