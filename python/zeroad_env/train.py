"""Pointer policy + clipped PPO against the headless ZeroAD encounter (shm + Petra)."""

from __future__ import annotations

import argparse
from collections.abc import Mapping

import numpy as np

from zeroad_env.gym_env import ZeroADGymEnv
from zeroad_env import layout as L

OWN = 1
ENEMY = 3
FN_TRAIN = 7


def _own_mask(ent: np.ndarray) -> np.ndarray:
    valid = ent[:, 0] > 0
    return valid & (ent[:, 1] == OWN)


def _tgt_mask(ent: np.ndarray, fn: int) -> np.ndarray:
    valid = ent[:, 0] > 0
    own = ent[:, 1] == OWN
    if fn in (3, 11):  # Attack, AttackWalk prefers enemies
        m = valid & (ent[:, 1] == ENEMY)
        if m.any():
            return m
    if fn == 4:  # Gather: gaia / not-own
        m = valid & ~own
        if m.any():
            return m
    m = valid & ~own
    return m if m.any() else valid


def _cell_toward(obs: Mapping[str, np.ndarray], selected: int, target: int) -> tuple[int, int]:
    ent = obs["entities"]
    if 0 <= target < L.MAX_ENTITIES and ent[target, 0] > 0:
        return int(ent[target, 3]), int(ent[target, 4])
    if 0 <= selected < L.MAX_ENTITIES and ent[selected, 0] > 0:
        return int(ent[selected, 3]), int(ent[selected, 4])
    return L.SPATIAL_SIZE // 2, L.SPATIAL_SIZE // 2


def _catalog(obs: Mapping[str, np.ndarray], fn: int) -> int:
    """Train copies the first own attacker template id; Build/Research stay 0."""
    if fn != FN_TRAIN:
        return 0
    ent = obs["entities"]
    for i in range(L.MAX_ENTITIES):
        if ent[i, 0] == 0:
            continue
        if ent[i, 1] == OWN and (int(ent[i, 8]) & 2) != 0:
            return int(ent[i, 2])
    return 0


def _invert_owner(obs: Mapping[str, np.ndarray]) -> dict[str, np.ndarray]:
    """Swap self/enemy owner-rel so the same policy can play player 2."""
    ent = np.array(obs["entities"], copy=True)
    rel = ent[:, 1]
    own = rel == OWN
    enemy = rel == ENEMY
    rel[own] = ENEMY
    rel[enemy] = OWN
    ent[:, 1] = rel
    out = dict(obs)
    out["entities"] = ent
    return out


class Policy:
    """Function head + entity pointers + value, all from a pooled entity encoder."""

    def __init__(self, torch_mod: object, nn_mod: object) -> None:
        self.torch = torch_mod
        nn = nn_mod
        self.ent = nn.Sequential(nn.Linear(L.ENTITY_FEAT, 32), nn.Tanh())
        self.body = nn.Sequential(nn.Linear(32 + L.SCALAR_COUNT, 64), nn.Tanh())
        self.fn = nn.Linear(64, L.N_FUNCTIONS)
        self.sel = nn.Linear(32, 1)
        self.tgt = nn.Linear(32, 1)
        self.val = nn.Linear(64, 1)
        self._params = (
            list(self.ent.parameters())
            + list(self.body.parameters())
            + list(self.fn.parameters())
            + list(self.sel.parameters())
            + list(self.tgt.parameters())
            + list(self.val.parameters())
        )

    def parameters(self) -> list[object]:
        return self._params

    def _forward(self, obs: Mapping[str, np.ndarray]):
        torch = self.torch
        ent = torch.from_numpy(obs["entities"].astype(np.float32))
        valid = ent[:, 0]
        enc = self.ent(ent)
        count = valid.sum().clamp(min=1.0)
        pooled = (enc * valid.unsqueeze(-1)).sum(0) / count
        scalars = torch.from_numpy(obs["scalars"].astype(np.float32))
        h = self.body(torch.cat([pooled, scalars], 0))
        fn_logits = self.fn(h)
        mask = torch.from_numpy(obs["function_mask"][: L.N_FUNCTIONS].astype(np.float32))
        fn_logits = fn_logits + (mask - 1.0) * 1e9
        sel_logits = self.sel(enc).squeeze(-1)
        tgt_logits = self.tgt(enc).squeeze(-1)
        v = self.val(h).squeeze()
        return fn_logits, sel_logits, tgt_logits, v, ent

    def dist(self, obs: Mapping[str, np.ndarray], fn: int | None = None):
        torch = self.torch
        from torch.distributions import Categorical

        fn_logits, sel_logits, tgt_logits, v, ent = self._forward(obs)
        ent_np = obs["entities"]
        own = torch.from_numpy(_own_mask(ent_np).astype(np.float32))
        if own.sum() <= 0:
            own = (ent[:, 0] > 0).float()
        sel_logits = sel_logits + (own - 1.0) * 1e9
        fn_dist = Categorical(logits=fn_logits)
        if fn is None:
            fn_t = fn_dist.sample()
            fn_i = int(fn_t.item())
        else:
            fn_i = fn
            fn_t = torch.tensor(fn_i)
        tgt_np = _tgt_mask(ent_np, fn_i).astype(np.float32)
        tgt_m = torch.from_numpy(tgt_np)
        tgt_logits = tgt_logits + (tgt_m - 1.0) * 1e9
        sel_dist = Categorical(logits=sel_logits)
        tgt_dist = Categorical(logits=tgt_logits)
        return fn_dist, sel_dist, tgt_dist, fn_t, v


def _discounted(rewards: list[float], gamma: float) -> np.ndarray:
    ret = 0.0
    out = np.zeros(len(rewards), dtype=np.float32)
    for i in range(len(rewards) - 1, -1, -1):
        ret = rewards[i] + gamma * ret
        out[i] = ret
    return out


def train(
    episodes: int,
    max_steps: int,
    seed: int,
    lr: float,
    privileged: bool,
    ppo_epochs: int,
    gamma: float,
    self_play: bool,
) -> None:
    """Run a tiny pointer-policy trainer. Requires PyTorch."""
    import torch
    from torch import nn

    if self_play:
        privileged = True
    env = ZeroADGymEnv(
        backend="shm",
        seed=seed,
        privileged=privileged,
        step_mul=8,
        petra=not self_play,
        max_turns=10_000,
        render_mode="rgb_array",
    )
    policy = Policy(torch, nn)
    opt = torch.optim.Adam(policy.parameters(), lr=lr)
    returns: list[float] = []
    try:
        for ep in range(episodes):
            obs, _info = env.reset()
            ep_obs: list[dict[str, np.ndarray]] = []
            fns: list[int] = []
            sels: list[int] = []
            tgts: list[int] = []
            old_logps: list[torch.Tensor] = []
            values: list[torch.Tensor] = []
            rewards: list[float] = []
            terminated = False
            for _ in range(max_steps):
                fn_dist, sel_dist, tgt_dist, fn_t, v = policy.dist(obs)
                fn_i = int(fn_t.item())
                sel_t = sel_dist.sample()
                tgt_t = tgt_dist.sample()
                logp = fn_dist.log_prob(fn_t) + sel_dist.log_prob(sel_t) + tgt_dist.log_prob(tgt_t)
                sel_i = int(sel_t.item())
                tgt_i = int(tgt_t.item())
                cell_x, cell_z = _cell_toward(obs, sel_i, tgt_i)
                action = {
                    "function": fn_i,
                    "selected": sel_i,
                    "target": tgt_i,
                    "cell_x": cell_x,
                    "cell_z": cell_z,
                    "catalog": _catalog(obs, fn_i),
                    "opp_function": 0,
                    "opp_selected": -1,
                    "opp_target": -1,
                }
                if self_play:
                    opp_obs = _invert_owner(obs)
                    o_fn_d, o_sel_d, o_tgt_d, o_fn_t, o_v = policy.dist(opp_obs)
                    o_fn_i = int(o_fn_t.item())
                    o_sel_t = o_sel_d.sample()
                    o_tgt_t = o_tgt_d.sample()
                    o_logp = (
                        o_fn_d.log_prob(o_fn_t)
                        + o_sel_d.log_prob(o_sel_t)
                        + o_tgt_d.log_prob(o_tgt_t)
                    )
                    action["opp_function"] = o_fn_i
                    action["opp_selected"] = int(o_sel_t.item())
                    action["opp_target"] = int(o_tgt_t.item())
                    ep_obs.append(opp_obs)
                    fns.append(o_fn_i)
                    sels.append(int(o_sel_t.item()))
                    tgts.append(int(o_tgt_t.item()))
                    old_logps.append(o_logp)
                    values.append(o_v)
                ep_obs.append(obs)
                fns.append(fn_i)
                sels.append(sel_i)
                tgts.append(tgt_i)
                old_logps.append(logp)
                values.append(v)
                obs, reward, terminated, _trunc, _info = env.step(action)
                r = float(reward)
                if self_play:
                    rewards.append(-r)
                rewards.append(r)
                if terminated:
                    break
            ep_ret = float(sum(rewards[1::2] if self_play else rewards))
            if old_logps:
                ret = torch.from_numpy(_discounted(rewards, gamma))
                val = torch.stack(values)
                adv = ret - val.detach()
                std = adv.std()
                if bool(torch.isfinite(std)) and float(std) > 1e-6:
                    adv = (adv - adv.mean()) / (std + 1e-8)
                old = torch.stack(old_logps).detach()
                for _ in range(max(1, ppo_epochs)):
                    new_logps = []
                    new_vs = []
                    for i, o in enumerate(ep_obs):
                        fn_d, sel_d, tgt_d, _, v_new = policy.dist(o, fn=fns[i])
                        fn_i = torch.tensor(fns[i])
                        sel_i = torch.tensor(sels[i])
                        tgt_i = torch.tensor(tgts[i])
                        new_logps.append(
                            fn_d.log_prob(fn_i) + sel_d.log_prob(sel_i) + tgt_d.log_prob(tgt_i)
                        )
                        new_vs.append(v_new)
                    new_lp = torch.stack(new_logps)
                    ratio = (new_lp - old).exp()
                    surr1 = ratio * adv
                    surr2 = ratio.clamp(1.0 - 0.2, 1.0 + 0.2) * adv
                    pg = -torch.min(surr1, surr2).mean()
                    vf = 0.5 * (torch.stack(new_vs) - ret).pow(2).mean()
                    loss = pg + vf
                    opt.zero_grad()
                    loss.backward()
                    opt.step()
            returns.append(ep_ret)
            print(
                f"episode {ep + 1}/{episodes} return={ep_ret:.1f} "
                f"done={terminated} mean={float(np.mean(returns)):.3f}"
                f"{' self-play' if self_play else ''}"
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
    parser.add_argument("--ppo-epochs", type=int, default=4)
    parser.add_argument("--gamma", type=float, default=0.99)
    parser.add_argument("--fog", action="store_true", help="disable privileged vision")
    parser.add_argument(
        "--self-play",
        action="store_true",
        help="same policy controls both players (privileged, no Petra)",
    )
    args = parser.parse_args()
    train(
        args.episodes,
        args.steps,
        args.seed,
        args.lr,
        privileged=not args.fog,
        ppo_epochs=args.ppo_epochs,
        gamma=args.gamma,
        self_play=args.self_play,
    )


if __name__ == "__main__":
    main()
