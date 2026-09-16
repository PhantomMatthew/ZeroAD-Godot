"""Thin Gymnasium-compatible wrapper. Default backend is shm (training); grpc is optional."""

from __future__ import annotations

from typing import Any, ClassVar

import numpy as np

from zeroad_env import layout as L


try:
    import gymnasium as gym
    from gymnasium import spaces
except ImportError:
    gym = None
    spaces = None


def _pad_selected(value: object) -> np.ndarray:
    out = np.full(L.MAX_SELECTED, -1, dtype=np.int32)
    if value is None:
        return out
    if isinstance(value, (list, tuple, np.ndarray)):
        arr = np.asarray(value, dtype=np.int32).ravel()
        n = min(L.MAX_SELECTED, arr.size)
        out[:n] = arr[:n]
        return out
    out[0] = int(value)
    return out


def _batch_from_action(action: dict[str, Any] | None) -> dict[str, np.ndarray] | None:
    if action is None:
        return None
    return {
        "function": np.array([action.get("function", 0)], dtype=np.int32),
        "selected": _pad_selected(action.get("selected", -1))[np.newaxis, :],
        "target": np.array([action.get("target", -1)], dtype=np.int32),
        "cell_x": np.array([action.get("cell_x", 0)], dtype=np.int32),
        "cell_z": np.array([action.get("cell_z", 0)], dtype=np.int32),
        "catalog": np.array([action.get("catalog", 0)], dtype=np.int32),
        "opp_function": np.array([action.get("opp_function", 0)], dtype=np.int32),
        "opp_selected": _pad_selected(action.get("opp_selected", -1))[np.newaxis, :],
        "opp_target": np.array([action.get("opp_target", -1)], dtype=np.int32),
        "opp_cell_x": np.array([action.get("opp_cell_x", 0)], dtype=np.int32),
        "opp_cell_z": np.array([action.get("opp_cell_z", 0)], dtype=np.int32),
        "opp_catalog": np.array([action.get("opp_catalog", 0)], dtype=np.int32),
    }


def _i32_box(shape: tuple[int, ...]) -> Any:
    lo = np.iinfo(np.int32).min
    hi = np.iinfo(np.int32).max
    return spaces.Box(low=lo, high=hi, shape=shape, dtype=np.int32)


class ZeroADGymEnv(gym.Env if gym is not None else object):
    """Single-slot env. ``backend='shm'`` for training, ``backend='grpc'`` for remote/debug."""

    metadata: ClassVar[dict[str, Any]] = {
        "render_modes": ["rgb_array"],
        "render_fps": 10,
    }

    def __init__(
        self, backend: str = "shm", render_mode: str | None = None, **kwargs: Any
    ) -> None:
        self._backend = backend
        self.render_mode = render_mode
        self._last_obs: dict[str, np.ndarray] | None = None
        if backend == "grpc":
            from zeroad_env.grpc_env import ZeroADGrpcEnv

            self._grpc = ZeroADGrpcEnv(**kwargs)
            self._vec = None
        elif backend == "shm":
            from zeroad_env.shm import ZeroADShmEnv

            kwargs.setdefault("n_slots", 1)
            self._vec = ZeroADShmEnv(**kwargs)
            self._grpc = None
        else:
            msg = f"unknown backend {backend!r}; use 'shm' or 'grpc'"
            raise ValueError(msg)
        if spaces is not None:
            self.observation_space = spaces.Dict(
                {
                    "entities": _i32_box((L.MAX_ENTITIES, L.ENTITY_FEAT)),
                    "spatial": _i32_box(
                        (L.SPATIAL_CHANNELS, L.SPATIAL_SIZE, L.SPATIAL_SIZE)
                    ),
                    "scalars": _i32_box((L.SCALAR_COUNT,)),
                    "function_mask": spaces.Box(
                        low=0, high=1, shape=(L.MASK_BYTES,), dtype=np.uint8
                    ),
                    "entity_mask": spaces.Box(
                        low=0,
                        high=np.iinfo(np.uint32).max,
                        shape=(L.MAX_ENTITIES,),
                        dtype=np.uint32,
                    ),
                    "catalog": _i32_box(
                        (L.MAX_ENTITIES, L.CATALOG_KIND_COUNT, L.MAX_CATALOG_CHOICES)
                    ),
                }
            )
            idx = spaces.Box(low=-1, high=L.MAX_ENTITIES - 1, shape=(), dtype=np.int32)
            self.action_space = spaces.Dict(
                {
                    "function": spaces.Discrete(L.N_FUNCTIONS),
                    "selected": spaces.Box(
                        low=-1,
                        high=L.MAX_ENTITIES - 1,
                        shape=(L.MAX_SELECTED,),
                        dtype=np.int32,
                    ),
                    "target": idx,
                    "cell_x": spaces.Discrete(L.SPATIAL_SIZE),
                    "cell_z": spaces.Discrete(L.SPATIAL_SIZE),
                    "catalog": spaces.Box(
                        low=0, high=2**31 - 1, shape=(), dtype=np.int32
                    ),
                }
            )

    @property
    def observation_shapes(self) -> dict[str, tuple[int, ...]]:
        """Shape dictionary when Gymnasium is not installed."""
        return {
            "entities": (L.MAX_ENTITIES, L.ENTITY_FEAT),
            "spatial": (L.SPATIAL_CHANNELS, L.SPATIAL_SIZE, L.SPATIAL_SIZE),
            "scalars": (L.SCALAR_COUNT,),
            "function_mask": (L.MASK_BYTES,),
            "entity_mask": (L.MAX_ENTITIES,),
            "catalog": (L.MAX_ENTITIES, L.CATALOG_KIND_COUNT, L.MAX_CATALOG_CHOICES),
        }

    def reset(
        self, seed: int | None = None, **kwargs: Any
    ) -> tuple[dict[str, np.ndarray], dict[str, Any]]:
        """Reset the match."""
        del kwargs
        if self._grpc is not None:
            obs = self._grpc.reset(seed=seed)
            self._last_obs = obs
            return obs, {}
        assert self._vec is not None
        obs = self._vec.reset()
        squeezed = {k: v[0] for k, v in obs.items()}
        self._last_obs = squeezed
        return squeezed, {}

    def step(
        self, action: dict[str, Any] | None = None
    ) -> tuple[dict[str, np.ndarray], float, bool, bool, dict[str, Any]]:
        """Step one env turn-mul. Returns obs, reward, terminated, truncated, info."""
        if self._grpc is not None:
            obs, reward, done, info = self._grpc.step(action)
            self._last_obs = obs
            return obs, reward, done, False, info
        assert self._vec is not None
        obs, reward, done, infos = self._vec.step(_batch_from_action(action))
        squeezed = {k: v[0] for k, v in obs.items()}
        self._last_obs = squeezed
        return squeezed, float(reward[0]), bool(done[0]), False, infos[0]

    def render(self) -> np.ndarray | None:
        """Return a 64×64 RGB view of the visibility spatial channel."""
        if self._last_obs is None:
            return None
        vis = self._last_obs["spatial"][0]
        rgb = np.zeros((L.SPATIAL_SIZE, L.SPATIAL_SIZE, 3), dtype=np.uint8)
        visible = vis == 2
        fog = vis == 1
        rgb[visible] = (220, 200, 140)
        rgb[fog] = (90, 90, 110)
        rgb[~visible & ~fog] = (20, 22, 28)
        return rgb

    def close(self) -> None:
        """Shut down the backend."""
        if self._grpc is not None:
            self._grpc.close()
        if self._vec is not None:
            self._vec.close()

    def set_max_turns(self, turns: int) -> None:
        """Forward curriculum cap to the shm host (no-op on gRPC)."""
        if self._vec is not None:
            self._vec.set_max_turns(turns)


def make_env(backend: str = "shm", **kwargs: Any) -> ZeroADGymEnv:
    """Create a gym env (default backend is shm)."""
    return ZeroADGymEnv(backend=backend, **kwargs)
