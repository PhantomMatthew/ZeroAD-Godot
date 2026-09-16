"""Gymnasium-style env that talks to ZeroAD.RlHost over a file-backed mmap."""

from __future__ import annotations

import contextlib
import mmap
import os
import struct
import subprocess
import tempfile
import time
from pathlib import Path
from typing import Any

import numpy as np

from zeroad_env import layout as L
from zeroad_env.host import host_command


_I32 = struct.Struct("<i")
_U32 = struct.Struct("<I")


class ZeroADShmEnv:
    """Vector of ``n_slots`` headless matches. Observations are int32 numpy arrays."""

    def __init__(
        self,
        n_slots: int = 1,
        *,
        seed: int = 1,
        privileged: bool = False,
        step_mul: int = 1,
        shm_path: str | None = None,
        petra: bool = True,
        max_turns: int = 10_000,
        map_name: str = "",
        map_size: int = 128,
        agent_civ: str = "athen",
        opponent_civ: str = "athen",
        random_civs: bool = False,
    ) -> None:
        self.n_slots = max(1, n_slots)
        self._seed = seed
        self._max_turns = max(1, max_turns)
        path = Path(shm_path) if shm_path else Path(
            tempfile.gettempdir(), f"zeroad_rl_{os.getpid()}.shm"
        )
        self._path = path
        cmd = [
            *host_command(),
            "--shm",
            str(path),
            "--slots",
            str(self.n_slots),
            "--seed",
            str(seed),
            "--step-mul",
            str(step_mul),
            "--max-turns",
            str(self._max_turns),
        ]
        if privileged:
            cmd.append("--privileged")
        cmd.append("--petra" if petra else "--no-petra")
        if map_name:
            cmd.extend(["--map", map_name, "--map-size", str(map_size)])
        if random_civs:
            cmd.append("--random-civs")
        else:
            cmd.extend(["--agent-civ", agent_civ, "--opponent-civ", opponent_civ])
        self._proc = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
        assert self._proc.stdout is not None
        deadline = time.time() + 60
        ready = ""
        while time.time() < deadline:
            line = self._proc.stdout.readline()
            if not line:
                err = self._proc.stderr.read() if self._proc.stderr else ""
                msg = f"RlHost exited before READY: {err}"
                raise RuntimeError(msg)
            if line.startswith("READY"):
                ready = line
                break
        if not ready:
            self.close()
            msg = "RlHost did not print READY"
            raise TimeoutError(msg)
        size = L.file_bytes(self.n_slots)
        self._fd = os.open(str(path), os.O_RDWR)
        self._mm = mmap.mmap(self._fd, size)
        magic = _U32.unpack_from(self._mm, L.OFF_MAGIC)[0]
        version = _I32.unpack_from(self._mm, L.OFF_VERSION)[0]
        if magic != L.MAGIC or version != L.VERSION:
            self.close()
            msg = f"layout mismatch magic={magic:#x} version={version}"
            raise RuntimeError(msg)

    def reset(self) -> dict[str, np.ndarray]:
        """Reset every slot and return batched observations."""
        _I32.pack_into(self._mm, L.OFF_MAX_TURNS, int(self._max_turns))
        self._issue(L.CMD_RESET)
        return self._read_obs()

    def set_max_turns(self, turns: int) -> None:
        """Curriculum: next reset uses this episode cap (host reads header)."""
        self._max_turns = max(1, int(turns))

    def step(self, actions: dict[str, np.ndarray] | None = None) -> tuple[
        dict[str, np.ndarray], np.ndarray, np.ndarray, list[dict[str, Any]]
    ]:
        """Step all slots. Action dict keys match the frozen action struct."""
        if actions is None:
            actions = {
                "function": np.zeros(self.n_slots, dtype=np.int32),
                "selected": np.full((self.n_slots, L.MAX_SELECTED), -1, dtype=np.int32),
                "target": np.full(self.n_slots, -1, dtype=np.int32),
                "cell_x": np.zeros(self.n_slots, dtype=np.int32),
                "cell_z": np.zeros(self.n_slots, dtype=np.int32),
                "catalog": np.zeros(self.n_slots, dtype=np.int32),
            }
        zeros = np.zeros(self.n_slots, dtype=np.int32)
        neg = np.full(self.n_slots, -1, dtype=np.int32)
        sel = _selected_batch(actions.get("selected"), self.n_slots)
        opp_sel = _selected_batch(actions.get("opp_selected"), self.n_slots)
        opp_fn = actions.get("opp_function", zeros)
        opp_tgt = actions.get("opp_target", neg)
        opp_cx = actions.get("opp_cell_x", zeros)
        opp_cz = actions.get("opp_cell_z", zeros)
        opp_cat = actions.get("opp_catalog", zeros)
        for s in range(self.n_slots):
            base = L.slot_offset(s) + L.OFF_ACTION
            _write_action_block(
                self._mm,
                base,
                int(actions["function"][s]),
                sel[s],
                int(actions["target"][s]),
                int(actions["cell_x"][s]),
                int(actions["cell_z"][s]),
                int(actions["catalog"][s]),
            )
            _write_action_block(
                self._mm,
                base + L.ACT_OPP_BASE,
                int(opp_fn[s]),
                opp_sel[s],
                int(opp_tgt[s]),
                int(opp_cx[s]),
                int(opp_cz[s]),
                int(opp_cat[s]),
            )
        self._issue(L.CMD_STEP)
        obs = self._read_obs()
        reward = np.empty(self.n_slots, dtype=np.int32)
        done = np.empty(self.n_slots, dtype=np.bool_)
        infos: list[dict[str, Any]] = []
        for s in range(self.n_slots):
            off = L.slot_offset(s)
            reward[s] = _I32.unpack_from(self._mm, off + L.OFF_REWARD)[0]
            done[s] = _I32.unpack_from(self._mm, off + L.OFF_DONE)[0] != 0
            turn = _I32.unpack_from(self._mm, off + L.OFF_TURN)[0]
            infos.append({"turn": turn})
        return obs, reward, done, infos

    def close(self) -> None:
        """Stop the host and unmap the file."""
        try:
            if getattr(self, "_mm", None) is not None:
                _I32.pack_into(self._mm, L.OFF_CMD, L.CMD_QUIT)
                seq = _I32.unpack_from(self._mm, L.OFF_SEQ_IN)[0] + 1
                _I32.pack_into(self._mm, L.OFF_SEQ_IN, seq)
                self._mm.flush()
                self._mm.close()
        except (BufferError, ValueError, OSError):
            pass
        proc = getattr(self, "_proc", None)
        if proc is not None and proc.poll() is None:
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                proc.kill()
        fd = getattr(self, "_fd", None)
        if fd is not None:
            os.close(fd)
        path = getattr(self, "_path", None)
        if path is not None:
            with contextlib.suppress(OSError):
                path.unlink()

    def _issue(self, cmd: int) -> None:
        _I32.pack_into(self._mm, L.OFF_CMD, cmd)
        seq = _I32.unpack_from(self._mm, L.OFF_SEQ_IN)[0] + 1
        _I32.pack_into(self._mm, L.OFF_SEQ_IN, seq)
        self._mm.flush()
        deadline = time.time() + 120
        while time.time() < deadline:
            out = _I32.unpack_from(self._mm, L.OFF_SEQ_OUT)[0]
            if out == seq:
                return
            time.sleep(0.001)
        msg = "RlHost step timed out"
        raise TimeoutError(msg)

    def _read_obs(self) -> dict[str, np.ndarray]:
        entities = np.empty((self.n_slots, L.MAX_ENTITIES, L.ENTITY_FEAT), dtype=np.int32)
        spatial = np.empty(
            (self.n_slots, L.SPATIAL_CHANNELS, L.SPATIAL_SIZE, L.SPATIAL_SIZE),
            dtype=np.int32,
        )
        scalars = np.empty((self.n_slots, L.SCALAR_COUNT), dtype=np.int32)
        mask = np.empty((self.n_slots, L.MASK_BYTES), dtype=np.uint8)
        entity_mask = np.empty((self.n_slots, L.MAX_ENTITIES), dtype=np.uint32)
        catalog = np.empty(
            (self.n_slots, L.MAX_ENTITIES, L.CATALOG_KIND_COUNT, L.MAX_CATALOG_CHOICES),
            dtype=np.int32,
        )
        for s in range(self.n_slots):
            off = L.slot_offset(s)
            entities[s] = np.frombuffer(
                self._mm,
                dtype="<i4",
                count=L.MAX_ENTITIES * L.ENTITY_FEAT,
                offset=off + L.OFF_ENTITIES,
            ).reshape(L.MAX_ENTITIES, L.ENTITY_FEAT)
            spatial[s] = np.frombuffer(
                self._mm,
                dtype="<i4",
                count=L.SPATIAL_CHANNELS * L.SPATIAL_SIZE * L.SPATIAL_SIZE,
                offset=off + L.OFF_SPATIAL,
            ).reshape(L.SPATIAL_CHANNELS, L.SPATIAL_SIZE, L.SPATIAL_SIZE)
            scalars[s] = np.frombuffer(
                self._mm,
                dtype="<i4",
                count=L.SCALAR_COUNT,
                offset=off + L.OFF_SCALARS,
            )
            mask[s] = np.frombuffer(
                self._mm,
                dtype=np.uint8,
                count=L.MASK_BYTES,
                offset=off + L.OFF_MASK,
            )
            entity_mask[s] = np.frombuffer(
                self._mm,
                dtype="<u4",
                count=L.MAX_ENTITIES,
                offset=off + L.OFF_ENTITY_MASK,
            )
            catalog[s] = np.frombuffer(
                self._mm,
                dtype="<i4",
                count=L.MAX_ENTITIES * L.CATALOG_KIND_COUNT * L.MAX_CATALOG_CHOICES,
                offset=off + L.OFF_CATALOG,
            ).reshape(L.MAX_ENTITIES, L.CATALOG_KIND_COUNT, L.MAX_CATALOG_CHOICES)
        return {
            "entities": entities.copy(),
            "spatial": spatial.copy(),
            "scalars": scalars.copy(),
            "function_mask": mask.copy(),
            "entity_mask": entity_mask.copy(),
            "catalog": catalog.copy(),
        }


def _selected_batch(value: object, n_slots: int) -> np.ndarray:
    """Accept ``(n_slots,)`` or ``(n_slots, 8)`` selected indices; pad unused with -1."""
    out = np.full((n_slots, L.MAX_SELECTED), -1, dtype=np.int32)
    if value is None:
        return out
    arr = np.asarray(value, dtype=np.int32)
    if arr.ndim == 0:
        out[0, 0] = int(arr)
        return out
    if arr.ndim == 1:
        n = min(n_slots, arr.shape[0])
        out[:n, 0] = arr[:n]
        return out
    n = min(n_slots, arr.shape[0])
    k = min(L.MAX_SELECTED, arr.shape[1])
    out[:n, :k] = arr[:n, :k]
    return out


def _write_action_block(
    mm: mmap.mmap,
    base: int,
    function: int,
    selected: np.ndarray,
    target: int,
    cell_x: int,
    cell_z: int,
    catalog: int,
) -> None:
    _I32.pack_into(mm, base + L.ACT_FUNCTION, function)
    _I32.pack_into(mm, base + L.ACT_TARGET, target)
    _I32.pack_into(mm, base + L.ACT_CELL_X, cell_x)
    _I32.pack_into(mm, base + L.ACT_CELL_Z, cell_z)
    _I32.pack_into(mm, base + L.ACT_CATALOG, catalog)
    for i in range(L.MAX_SELECTED):
        _I32.pack_into(mm, base + L.ACT_SELECTED0 + i * 4, int(selected[i]))
