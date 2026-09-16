"""Pointer policy + clipped PPO against the headless ZeroAD encounter (shm + Petra)."""

from __future__ import annotations

import argparse
from collections.abc import Mapping
from pathlib import Path
from typing import Any

import numpy as np

from zeroad_env import layout as L
from zeroad_env.catalog import (
    CELL_FNS,
    CATALOG_FNS,
    PACKED_CATALOG,
    Catalog,
    legal_catalog_ids,
)
from zeroad_env.gym_env import ZeroADGymEnv


OWN = 1
ENEMY = 3
CAT_EMB = 16384
# ActionTranslator.FansOut plus Formation (one command over every filled slot).
FANOUT_FNS = frozenset({1, 2, 3, 4, 5, 9, 10, 11, 12, 15, 16, 17, 19, 22, 23, 26})


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


def _fn_legal_own(obs: Mapping[str, np.ndarray], fn: int) -> np.ndarray:
    own = _own_mask(obs["entities"])
    em = obs.get("entity_mask")
    if em is None:
        return own
    bits = ((em.astype(np.uint32) >> int(fn)) & 1) != 0
    n = own.shape[0]
    bits = bits[:n]
    legal = own & bits
    return legal if legal.any() else own


def _avail_mask(legal: np.ndarray, n: int, torch_mod: object):
    """Float mask of still-legal entity rows (1 = available)."""
    out = np.zeros(n, dtype=np.float32)
    src = np.asarray(legal, dtype=np.float32).ravel()
    k = min(n, src.size)
    out[:k] = src[:k]
    return torch_mod.from_numpy(out)


def sequential_sample_selected(
    sel_logits, legal: np.ndarray, n_slots: int, torch_mod: object
) -> np.ndarray:
    """Sample up to ``n_slots`` distinct legal rows (Plackett–Luce / no replacement)."""
    from torch.distributions import Categorical

    out = np.full(L.MAX_SELECTED, -1, dtype=np.int32)
    n = int(sel_logits.shape[0])
    avail = _avail_mask(legal, n, torch_mod)
    k = max(0, min(int(n_slots), L.MAX_SELECTED))
    for slot in range(k):
        if float(avail.sum()) <= 0:
            break
        dist = Categorical(logits=sel_logits + (avail - 1.0) * 1e9)
        idx_t = dist.sample()
        idx = int(idx_t.item())
        out[slot] = idx
        avail = avail.clone()
        avail[idx] = 0.0
    return out


def sequential_selected_logprob(
    sel_logits, legal: np.ndarray, selected: np.ndarray, torch_mod: object
):
    """Joint log π(selected[0], selected[1], …) under sequential without-replacement."""
    from torch.distributions import Categorical

    n = int(sel_logits.shape[0])
    avail = _avail_mask(legal, n, torch_mod)
    lp = sel_logits.new_zeros(())
    for raw in selected:
        idx = int(raw)
        if idx < 0:
            break
        dist = Categorical(logits=sel_logits + (avail - 1.0) * 1e9)
        lp = lp + dist.log_prob(torch_mod.tensor(idx, dtype=torch_mod.long))
        avail = avail.clone()
        avail[idx] = 0.0
    return lp


def _function_mask_from_own(
    ent: np.ndarray, entity_mask: np.ndarray
) -> np.ndarray:
    mask = np.zeros(L.N_FUNCTIONS, dtype=np.uint8)
    mask[0] = 1
    own = _own_mask(ent)
    if not own.any():
        return mask
    ored = int(np.bitwise_or.reduce(entity_mask[own].astype(np.uint32)))
    for f in range(L.N_FUNCTIONS):
        if ored & (1 << f):
            mask[f] = 1
    return mask


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
    em = obs.get("entity_mask")
    if em is not None:
        out["function_mask"] = _function_mask_from_own(ent, np.asarray(em))
    return out


class Policy:
    """Function, entity pointers, map cells, catalog pointer, and value."""

    def __init__(self, torch_mod: object, nn_mod: object, catalog: Catalog) -> None:
        self.torch = torch_mod
        nn = nn_mod
        self.catalog = catalog
        self.ent = nn.Sequential(nn.Linear(L.ENTITY_FEAT, 32), nn.Tanh())
        self.body = nn.Sequential(
            nn.Linear(32 + L.SCALAR_COUNT + L.SPATIAL_CHANNELS, 64), nn.Tanh()
        )
        self.fn = nn.Linear(64, L.N_FUNCTIONS)
        self.sel = nn.Linear(32, 1)
        self.tgt = nn.Linear(32, 1)
        self.cell_x = nn.Linear(64, L.SPATIAL_SIZE)
        self.cell_z = nn.Linear(64, L.SPATIAL_SIZE)
        self.cat_q = nn.Linear(64, 16)
        self.cat_emb = nn.Embedding(CAT_EMB, 16)
        self.val = nn.Linear(64, 1)
        self._params = (
            list(self.ent.parameters())
            + list(self.body.parameters())
            + list(self.fn.parameters())
            + list(self.sel.parameters())
            + list(self.tgt.parameters())
            + list(self.cell_x.parameters())
            + list(self.cell_z.parameters())
            + list(self.cat_q.parameters())
            + list(self.cat_emb.parameters())
            + list(self.val.parameters())
        )

    def parameters(self) -> list[object]:
        return self._params

    def named_modules(self) -> dict[str, object]:
        return {
            "ent": self.ent,
            "body": self.body,
            "fn": self.fn,
            "sel": self.sel,
            "tgt": self.tgt,
            "cell_x": self.cell_x,
            "cell_z": self.cell_z,
            "cat_q": self.cat_q,
            "cat_emb": self.cat_emb,
            "val": self.val,
        }

    def state_dict(self) -> dict[str, object]:
        return {k: m.state_dict() for k, m in self.named_modules().items()}

    def load_state_dict(self, blob: Mapping[str, object]) -> None:
        for k, m in self.named_modules().items():
            if k in blob:
                m.load_state_dict(blob[k])

    def _forward(self, obs: Mapping[str, np.ndarray]):
        torch = self.torch
        ent = torch.from_numpy(obs["entities"].astype(np.float32))
        valid = ent[:, 0]
        enc = self.ent(ent)
        count = valid.sum().clamp(min=1.0)
        pooled = (enc * valid.unsqueeze(-1)).sum(0) / count
        scalars = torch.from_numpy(obs["scalars"].astype(np.float32))
        spat = torch.from_numpy(
            obs["spatial"].reshape(L.SPATIAL_CHANNELS, -1).astype(np.float32)
        )
        spat_h = spat.mean(dim=1)
        h = self.body(torch.cat([pooled, scalars, spat_h], 0))
        fn_logits = self.fn(h)
        mask = torch.from_numpy(obs["function_mask"][: L.N_FUNCTIONS].astype(np.float32))
        fn_logits = fn_logits + (mask - 1.0) * 1e9
        sel_logits = self.sel(enc).squeeze(-1)
        tgt_logits = self.tgt(enc).squeeze(-1)
        cx_logits = self.cell_x(h)
        cz_logits = self.cell_z(h)
        v = self.val(h).squeeze()
        return h, fn_logits, sel_logits, tgt_logits, cx_logits, cz_logits, v

    def _pointer_dists(self, obs: Mapping[str, np.ndarray], fn_i: int, sel_logits, tgt_logits):
        torch = self.torch
        from torch.distributions import Categorical

        ent_np = obs["entities"]
        own = torch.from_numpy(_own_mask(ent_np).astype(np.float32))
        if own.sum() <= 0:
            own = torch.from_numpy((ent_np[:, 0] > 0).astype(np.float32))
        legal = torch.from_numpy(_fn_legal_own(obs, fn_i).astype(np.float32))
        if legal.sum() <= 0:
            legal = own
        sel_logits = sel_logits + (legal - 1.0) * 1e9
        tgt_np = _tgt_mask(ent_np, fn_i).astype(np.float32)
        tgt_logits = tgt_logits + (torch.from_numpy(tgt_np) - 1.0) * 1e9
        sel_dist = Categorical(logits=sel_logits)
        tgt_dist = Categorical(logits=tgt_logits)
        return sel_dist, tgt_dist, sel_logits

    def catalog_dist(self, h, obs: Mapping[str, np.ndarray], fn: int, selected: int):
        """Categorical over legal intern ids, or None when catalog is unused/empty."""
        from torch.distributions import Categorical

        if fn in PACKED_CATALOG:
            ids = PACKED_CATALOG[fn]
        elif fn not in CATALOG_FNS:
            return None, []
        else:
            ids = legal_catalog_ids(obs, fn, selected, self.catalog)
        if not ids:
            return None, []
        torch = self.torch
        idx = torch.tensor([min(i, CAT_EMB - 1) for i in ids], dtype=torch.long)
        q = self.cat_q(h)
        scores = (self.cat_emb(idx) * q).sum(-1)
        return Categorical(logits=scores), ids

    def dist(self, obs: Mapping[str, np.ndarray], fn: int | None = None):
        torch = self.torch
        from torch.distributions import Categorical

        h, fn_logits, sel_logits, tgt_logits, cx_logits, cz_logits, v = self._forward(obs)
        fn_dist = Categorical(logits=fn_logits)
        if fn is None:
            fn_t = fn_dist.sample()
            fn_i = int(fn_t.item())
        else:
            fn_i = fn
            fn_t = torch.tensor(fn_i)
        sel_dist, tgt_dist, sel_masked = self._pointer_dists(
            obs, fn_i, sel_logits, tgt_logits
        )
        cx_dist = Categorical(logits=cx_logits)
        cz_dist = Categorical(logits=cz_logits)
        return {
            "h": h,
            "fn_dist": fn_dist,
            "sel_dist": sel_dist,
            "tgt_dist": tgt_dist,
            "sel_logits": sel_masked,
            "cx_dist": cx_dist,
            "cz_dist": cz_dist,
            "fn_t": fn_t,
            "fn": fn_i,
            "v": v,
        }


def action_logprob(
    policy: Policy,
    parts: dict[str, Any],
    obs: Mapping[str, np.ndarray],
    fn: int,
    selected: np.ndarray,
    tgt: int,
    cx: int,
    cz: int,
    cat: int,
):
    torch = policy.torch
    sel_arr = np.asarray(selected, dtype=np.int32).ravel()
    if sel_arr.size < L.MAX_SELECTED:
        pad = np.full(L.MAX_SELECTED, -1, dtype=np.int32)
        pad[: sel_arr.size] = sel_arr
        sel_arr = pad
    sel = int(sel_arr[0])
    legal = _fn_legal_own(obs, fn)
    lp = (
        parts["fn_dist"].log_prob(torch.tensor(fn))
        + sequential_selected_logprob(parts["sel_logits"], legal, sel_arr, torch)
        + parts["tgt_dist"].log_prob(torch.tensor(tgt))
    )
    if fn in CELL_FNS:
        lp = (
            lp
            + parts["cx_dist"].log_prob(torch.tensor(cx))
            + parts["cz_dist"].log_prob(torch.tensor(cz))
        )
    cat_dist, ids = policy.catalog_dist(parts["h"], obs, fn, sel)
    if cat_dist is not None and ids:
        try:
            idx = ids.index(cat)
        except ValueError:
            idx = 0
        lp = lp + cat_dist.log_prob(torch.tensor(idx))
    return lp


def _discounted(rewards: list[float], gamma: float) -> np.ndarray:
    ret = 0.0
    out = np.zeros(len(rewards), dtype=np.float32)
    for i in range(len(rewards) - 1, -1, -1):
        ret = rewards[i] + gamma * ret
        out[i] = ret
    return out


def _sample_side(policy: Policy, obs: Mapping[str, np.ndarray]) -> tuple[dict[str, Any], dict[str, Any]]:
    parts = policy.dist(obs)
    tgt_t = parts["tgt_dist"].sample()
    fn_i = parts["fn"]
    n_slots = L.MAX_SELECTED if fn_i in FANOUT_FNS else 1
    selected = sequential_sample_selected(
        parts["sel_logits"], _fn_legal_own(obs, fn_i), n_slots, policy.torch
    )
    sel_i = int(selected[0])
    tgt_i = int(tgt_t.item())
    if fn_i in CELL_FNS:
        cx_i = int(parts["cx_dist"].sample().item())
        cz_i = int(parts["cz_dist"].sample().item())
    else:
        cx_i, cz_i = 0, 0
    cat_dist, ids = policy.catalog_dist(parts["h"], obs, fn_i, sel_i)
    if cat_dist is not None and ids:
        cat_i = int(ids[int(cat_dist.sample().item())])
    else:
        cat_i = 0
    logp = action_logprob(policy, parts, obs, fn_i, selected, tgt_i, cx_i, cz_i, cat_i)
    rec = {
        "fn": fn_i,
        "sel": sel_i,
        "tgt": tgt_i,
        "cx": cx_i,
        "cz": cz_i,
        "cat": cat_i,
        "logp": logp,
        "v": parts["v"],
        "selected": selected,
    }
    return rec, parts


def _curriculum_turns(ep: int, episodes: int, base: int) -> int:
    """256 → 512 → 1024 → 2048 across the run (never below ``base`` if base is larger)."""
    stage = min(3, (ep * 4) // max(1, episodes))
    return max(base, (256, 512, 1024, 2048)[stage])


def _save_policy(policy: Policy, path: Path) -> None:
    import torch

    path.parent.mkdir(parents=True, exist_ok=True)
    torch.save(policy.state_dict(), path)


def _load_policy(policy: Policy, path: Path) -> None:
    import torch

    policy.load_state_dict(torch.load(path, map_location="cpu"))


def _vtrace(
    rewards: list[float],
    values: list[Any],
    rho: Any,
    gamma: float,
    bootstrap: float,
    clip: float = 1.0,
):
    """IMPALA V-trace targets (Espeholt et al.). ``rho`` is π/μ per step."""
    import torch

    r = torch.tensor(rewards, dtype=torch.float32)
    v = torch.stack(values)
    n = r.shape[0]
    vs = torch.zeros(n)
    cs = rho.clamp(max=clip)
    rho_c = rho.clamp(max=clip)
    nxt = torch.tensor(float(bootstrap), dtype=torch.float32)
    boot = nxt
    for t in range(n - 1, -1, -1):
        v_next = v[t + 1].detach() if t + 1 < n else boot
        delta = rho_c[t] * (r[t] + gamma * v_next - v[t].detach())
        vs[t] = v[t].detach() + delta.detach() + gamma * cs[t].detach() * (nxt - v_next)
        nxt = vs[t]
    last = torch.cat([vs[1:], boot.unsqueeze(0)])
    pg_adv = rho_c.detach() * (r + gamma * last - v.detach())
    return vs.detach(), pg_adv.detach()


def train(
    episodes: int,
    max_steps: int,
    seed: int,
    lr: float,
    privileged: bool,
    ppo_epochs: int,
    gamma: float,
    self_play: bool,
    max_turns: int = 512,
    curriculum: bool = False,
    map_name: str = "",
    map_size: int = 128,
    random_civs: bool = True,
    agent_civ: str = "athen",
    opponent_civ: str = "athen",
    league_dir: str = "",
    ckpt: str = "",
) -> None:
    """Run a tiny pointer-policy trainer. Requires PyTorch."""
    import torch
    from torch import nn

    env = ZeroADGymEnv(
        backend="shm",
        seed=seed,
        privileged=privileged,
        step_mul=8,
        petra=not self_play,
        max_turns=max_turns,
        map_name=map_name,
        map_size=map_size,
        random_civs=random_civs,
        agent_civ=agent_civ,
        opponent_civ=opponent_civ,
        render_mode="rgb_array",
    )
    catalog = Catalog.load()
    policy = Policy(torch, nn, catalog)
    if ckpt:
        _load_policy(policy, Path(ckpt))
    opt = torch.optim.Adam(policy.parameters(), lr=lr)
    returns: list[float] = []
    league = Path(league_dir) if league_dir else None
    try:
        for ep in range(episodes):
            if curriculum:
                env.set_max_turns(_curriculum_turns(ep, episodes, max_turns))
            obs, _info = env.reset()
            ep_obs: list[dict[str, np.ndarray]] = []
            recs: list[dict[str, Any]] = []
            old_logps: list[torch.Tensor] = []
            values: list[torch.Tensor] = []
            rewards: list[float] = []
            terminated = False
            for _ in range(max_steps):
                rec, _parts = _sample_side(policy, obs)
                action = {
                    "function": rec["fn"],
                    "selected": rec["selected"],
                    "target": rec["tgt"],
                    "cell_x": rec["cx"],
                    "cell_z": rec["cz"],
                    "catalog": rec["cat"],
                    "opp_function": 0,
                    "opp_selected": -1,
                    "opp_target": -1,
                }
                if self_play:
                    opp_obs = _invert_owner(obs)
                    o_rec, _ = _sample_side(policy, opp_obs)
                    action["opp_function"] = o_rec["fn"]
                    action["opp_selected"] = o_rec["selected"]
                    action["opp_target"] = o_rec["tgt"]
                    action["opp_cell_x"] = o_rec["cx"]
                    action["opp_cell_z"] = o_rec["cz"]
                    action["opp_catalog"] = o_rec["cat"]
                    ep_obs.append(opp_obs)
                    recs.append(o_rec)
                    old_logps.append(o_rec["logp"])
                    values.append(o_rec["v"])
                ep_obs.append(obs)
                recs.append(rec)
                old_logps.append(rec["logp"])
                values.append(rec["v"])
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
                        r = recs[i]
                        parts = policy.dist(o, fn=r["fn"])
                        new_logps.append(
                            action_logprob(
                                policy,
                                parts,
                                o,
                                r["fn"],
                                r["selected"],
                                r["tgt"],
                                r["cx"],
                                r["cz"],
                                r["cat"],
                            )
                        )
                        new_vs.append(parts["v"])
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
            if league is not None:
                _save_policy(policy, league / f"policy_{ep + 1:04d}.pt")
    finally:
        env.close()


def train_impala(
    episodes: int,
    max_steps: int,
    seed: int,
    lr: float,
    privileged: bool,
    gamma: float,
    n_slots: int,
    max_turns: int,
    map_name: str,
    map_size: int,
    random_civs: bool,
    league_dir: str,
) -> None:
    """Single-process IMPALA: vector shm actors + V-trace learner."""
    import torch
    from torch import nn

    from zeroad_env.shm import ZeroADShmEnv

    env = ZeroADShmEnv(
        n_slots=max(1, n_slots),
        seed=seed,
        privileged=privileged,
        step_mul=8,
        petra=True,
        max_turns=max_turns,
        map_name=map_name,
        map_size=map_size,
        random_civs=random_civs,
    )
    catalog = Catalog.load()
    policy = Policy(torch, nn, catalog)
    opt = torch.optim.Adam(policy.parameters(), lr=lr)
    league = Path(league_dir) if league_dir else None
    try:
        obs = env.reset()
        for ep in range(episodes):
            traj: list[list[dict[str, Any]]] = [[] for _ in range(env.n_slots)]
            done_mask = np.zeros(env.n_slots, dtype=bool)
            for _ in range(max_steps):
                recs = []
                fns = np.zeros(env.n_slots, dtype=np.int32)
                selected = np.full((env.n_slots, L.MAX_SELECTED), -1, dtype=np.int32)
                tgts = np.full(env.n_slots, -1, dtype=np.int32)
                cxs = np.zeros(env.n_slots, dtype=np.int32)
                czs = np.zeros(env.n_slots, dtype=np.int32)
                cats = np.zeros(env.n_slots, dtype=np.int32)
                for s in range(env.n_slots):
                    slot = {k: v[s] for k, v in obs.items()}
                    rec, _ = _sample_side(policy, slot)
                    recs.append((rec, slot))
                    fns[s] = rec["fn"]
                    selected[s] = rec["selected"]
                    tgts[s] = rec["tgt"]
                    cxs[s] = rec["cx"]
                    czs[s] = rec["cz"]
                    cats[s] = rec["cat"]
                obs, reward, done, _info = env.step(
                    {
                        "function": fns,
                        "selected": selected,
                        "target": tgts,
                        "cell_x": cxs,
                        "cell_z": czs,
                        "catalog": cats,
                    }
                )
                for s in range(env.n_slots):
                    if done_mask[s]:
                        continue
                    rec, slot_obs = recs[s]
                    traj[s].append(
                        {
                            "rec": rec,
                            "reward": float(reward[s]),
                            "obs": slot_obs,
                        }
                    )
                    if bool(done[s]):
                        done_mask[s] = True
                if bool(done_mask.all()):
                    break
            losses = []
            for s in range(env.n_slots):
                steps = traj[s]
                if len(steps) < 2:
                    continue
                rewards = [st["reward"] for st in steps]
                values = [st["rec"]["v"] for st in steps]
                old_lp = torch.stack([st["rec"]["logp"] for st in steps]).detach()
                new_lp = []
                new_v = []
                for st in steps:
                    r = st["rec"]
                    parts = policy.dist(st["obs"], fn=r["fn"])
                    new_lp.append(
                        action_logprob(
                            policy,
                            parts,
                            st["obs"],
                            r["fn"],
                            r["selected"],
                            r["tgt"],
                            r["cx"],
                            r["cz"],
                            r["cat"],
                        )
                    )
                    new_v.append(parts["v"])
                new_lp_t = torch.stack(new_lp)
                rho = (new_lp_t - old_lp).exp()
                vs, adv = _vtrace(rewards, new_v, rho, gamma, bootstrap=0.0)
                pg = -(rho.clamp(max=1.0).detach() * new_lp_t * adv).mean()
                vf = 0.5 * (torch.stack(new_v) - vs).pow(2).mean()
                losses.append(pg + vf)
            if losses:
                loss = torch.stack(losses).mean()
                opt.zero_grad()
                loss.backward()
                opt.step()
            mean_ret = float(np.mean([sum(st["reward"] for st in t) for t in traj if t] or [0.0]))
            print(f"impala {ep + 1}/{episodes} mean_return={mean_ret:.2f} slots={env.n_slots}")
            if league is not None:
                _save_policy(policy, league / f"impala_{ep + 1:04d}.pt")
            if bool(done_mask.all()):
                obs = env.reset()
                done_mask[:] = False
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
    parser.add_argument(
        "--privileged",
        action="store_true",
        help="full vision (default is fog of war)",
    )
    parser.add_argument(
        "--fog",
        action="store_true",
        help="deprecated: fog is already the default",
    )
    parser.add_argument(
        "--self-play",
        action="store_true",
        help="same policy controls both players (no Petra)",
    )
    parser.add_argument("--max-turns", type=int, default=512)
    parser.add_argument(
        "--curriculum",
        action="store_true",
        help="ramp max-turns 256→2048 across episodes",
    )
    parser.add_argument("--map", dest="map_name", default="", help="rmgen name or maps/... PMP")
    parser.add_argument("--map-size", type=int, default=128)
    parser.add_argument("--random-civs", action="store_true", default=True)
    parser.add_argument("--no-random-civs", action="store_false", dest="random_civs")
    parser.add_argument("--agent-civ", default="athen")
    parser.add_argument("--opponent-civ", default="athen")
    parser.add_argument("--algo", choices=("ppo", "impala"), default="ppo")
    parser.add_argument("--n-slots", type=int, default=4)
    parser.add_argument("--league", default="", help="directory to save / sample checkpoints")
    parser.add_argument("--ckpt", default="", help="load policy state_dict")
    args = parser.parse_args()
    privileged = args.privileged and not args.fog
    if args.algo == "impala":
        train_impala(
            args.episodes,
            args.steps,
            args.seed,
            args.lr,
            privileged,
            args.gamma,
            args.n_slots,
            args.max_turns,
            args.map_name,
            args.map_size,
            args.random_civs,
            args.league,
        )
        return
    train(
        args.episodes,
        args.steps,
        args.seed,
        args.lr,
        privileged=privileged,
        ppo_epochs=args.ppo_epochs,
        gamma=args.gamma,
        self_play=args.self_play,
        max_turns=args.max_turns,
        curriculum=args.curriculum,
        map_name=args.map_name,
        map_size=args.map_size,
        random_civs=args.random_civs,
        agent_civ=args.agent_civ,
        opponent_civ=args.opponent_civ,
        league_dir=args.league,
        ckpt=args.ckpt,
    )


if __name__ == "__main__":
    main()
